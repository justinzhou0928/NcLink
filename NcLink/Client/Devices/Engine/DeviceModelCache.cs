using NcLink.Client.Devices.Interfaces;
using NcLink.Client.Devices.Models;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NcLink.Exceptions;

namespace NcLink.Client.Devices.Engine;

/// <summary>
/// 设备模型缓存：命中则直接返回，未命中则探测并解析模型文件。
///
/// 并发去重用 <see cref="ConcurrentDictionary{TKey,TValue}"/> +
/// <c>Lazy&lt;Task&lt;T&gt;&gt;</c>（实测 8 个并发只探测 1 次）。
/// 不引入 <c>IMemoryCache</c>——设备模型不可淘汰，理由见 DEVELOPMENT.md「缓存」一节。
/// </summary>
internal sealed class DeviceModelCache
{
    private readonly IDeviceProbe _probe;
    private readonly ILogger<DeviceModelCache> _logger;

    /// <summary>
    /// 已就绪的模型。探测成功后写入，供只读查询（<see cref="RequireCached"/>、
    /// <see cref="GetChannelItemIds"/>）使用。
    /// </summary>
    private readonly ConcurrentDictionary<string, DeviceModel> _models = new(StringComparer.Ordinal);

    /// <summary>
    /// 在途探测。同一设备的并发请求共享同一个任务，避免重复的 MQTT 往返。
    /// 探测结束（成功或失败）后移除，失败时让后续调用能够重试。
    /// </summary>
    private readonly ConcurrentDictionary<string, Lazy<Task<DeviceModel>>> _inFlight = new(StringComparer.Ordinal);

    public DeviceModelCache(IDeviceProbe probe, ILogger<DeviceModelCache> logger)
    {
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>已缓存的设备数量。</summary>
    public int Count => _models.Count;

    /// <summary>已缓存的设备 SN。</summary>
    public IReadOnlyCollection<string> CachedDeviceIds => _models.Keys.ToArray();

    /// <summary>写入缓存（供外部注入已知模型）。</summary>
    public void Set(DeviceModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        _models[model.DeviceId] = model;
    }

    /// <summary>取模型；<paramref name="refresh"/> 为 true 时强制重新探测。</summary>
    public async Task<DeviceModel> GetAsync(
        string deviceId,
        bool refresh = false,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);

        if (!refresh && _models.TryGetValue(deviceId, out var cached))
            return cached;

        return await ProbeCoalescedAsync(deviceId, timeout, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>强制探测并解析设备模型（不与其他调用合并，调用方明确要求刷新）。</summary>
    public Task<DeviceModel> ProbeAsync(
        string deviceId,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        return ProbeCoreAsync(deviceId, timeout, cancellationToken);
    }

    /// <summary>取模型；未加载时抛 <see cref="NcLinkInvalidRequestException"/>（需先探测）。</summary>
    public DeviceModel RequireCached(string deviceId)
        => _models.TryGetValue(deviceId, out var model)
            ? model
            : throw new NcLinkInvalidRequestException($"设备 {deviceId} 的模型尚未加载，请先调用 ProbeAsync。");

    /// <summary>
    /// 取某设备某采样通道声明的数据项 id（顺序即报文 <c>data[]</c> 的顺序）。
    ///
    /// 采样报文本身不携带数据项 id，只能按位置与这份清单对应，
    /// 因此由这里提供映射依据。模型未加载或通道不存在时返回空。
    /// </summary>
    public IReadOnlyList<string> GetChannelItemIds(string deviceId, string channelId)
        => _models.TryGetValue(deviceId, out var model) &&
           model.FindSampleChannel(channelId) is { } channel
            ? channel.ItemIds
            : [];

    #region 内部

    /// <summary>
    /// 合并式探测：同一设备的并发调用共享同一次探测。
    ///
    /// 即使两个线程同时通过 <c>TryGetValue</c> 未命中并各自 <c>GetOrAdd</c>，
    /// <see cref="Lazy{T}"/> 也只会让其中一个执行工厂方法
    /// （另一个拿到已缓存的那个 <see cref="Lazy{T}"/> 实例）。
    ///
    /// <b>共享的探测任务刻意不接收任何调用方的取消令牌</b>：工厂只执行一次，
    /// 若把第一个调用方的令牌闭包进去，它就成了所有等待方共用的令牌——
    /// A 取消会把 B/C/D 一起打成取消，而它们的令牌从未取消过。
    /// 取消因此放在等待侧：每个调用方用自己的令牌 <c>WaitAsync</c>，
    /// 互不影响；探测本身则跑完并写进缓存（幂等，最多多一次 MQTT 往返）。
    /// </summary>
    private async Task<DeviceModel> ProbeCoalescedAsync(
        string deviceId, TimeSpan? timeout, CancellationToken cancellationToken)
    {
        var lazy = _inFlight.GetOrAdd(deviceId,
            id => new Lazy<Task<DeviceModel>>(
                () => ProbeCoreAsync(id, timeout, CancellationToken.None),
                LazyThreadSafetyMode.ExecutionAndPublication));

        try
        {
            return await lazy.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // 无论成败都移除：成功时模型已进 _models；失败时让后续调用可以重试，
            // 否则一次瞬时故障会被永久缓存成"该设备探测总是失败"。
            _inFlight.TryRemove(deviceId, out _);
        }
    }

    /// <summary>真正执行一次探测。</summary>
    private async Task<DeviceModel> ProbeCoreAsync(
        string deviceId, TimeSpan? timeout, CancellationToken cancellationToken)
    {
        var json = await _probe.ProbeModelJsonAsync(deviceId, timeout, cancellationToken).ConfigureAwait(false);

        if (!DeviceModelParser.TryParse(json, deviceId, out var model, out var error))
            throw new NcLinkProtocolException($"设备 {deviceId} 的模型文件解析失败：{error}", deviceId);

        _models[deviceId] = model!;
        return model!;
    }

    #endregion
}

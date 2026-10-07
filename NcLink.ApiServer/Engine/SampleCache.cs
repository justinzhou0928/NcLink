using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NcLink.Client.Query.Interfaces;
using NcLink.Client.Query.Models;
using NcLink.ApiServer.Models;
using NcLink.ApiServer.General.Logging;

namespace NcLink.ApiServer.Engine;

/// <summary>
/// 采样缓存：<b>服务端特有</b>的一块状态。
///
/// 它存在的唯一理由是 HTTP 客户端不能接收推送、只能轮询。
/// 官方 apiServer 的做法一致：`POST /v1/sample/{sn}/sub/{ch}` 建立订阅并返回 `sub_id`，
/// `GET /v1/sample/{sn}/data/{sub_id}` 取走已缓存的帧。
///
/// <b>为什么放在 ApiServer 而不是核心库</b>：库是推送式的（<c>ISampleSubscription.FrameReceived</c>），
/// 缓存是"为轮询服务的中间件职责"。放进核心库会给绝大多数消费者增加他们不需要的状态。
///
/// 取数语义为 <b>drain（取走并清空）</b>——实测官方行为：
/// 连续两次轮询拿到的是<b>不同的单帧</b>（各带不同 <c>beginTime</c>），
/// 而非累积的多帧，说明取走后即清空。
/// </summary>
internal sealed partial class SampleCache : IAsyncDisposable
{
    private readonly IQuery _query;
    private readonly IOptions<ApiServerOptions> _options;
    private readonly ILogger<SampleCache> _logger;
    private readonly TimeProvider _timeProvider;

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _reaper;

    public SampleCache(
        IQuery query,
        IOptions<ApiServerOptions> options,
        ILogger<SampleCache> logger,
        TimeProvider? timeProvider = null)
    {
        _query = query ?? throw new ArgumentNullException(nameof(query));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? TimeProvider.System;

        // 空闲回收：避免调用方忘记 unsubscribe 时无限增长
        _reaper = Task.Run(ReapLoopAsync);
    }

    /// <summary>当前活跃订阅数。</summary>
    public int Count => _entries.Count;

    /// <summary>建立订阅，返回 <c>sub_id</c>（官方返回 GUID 字符串）。</summary>
    public async Task<string> SubscribeAsync(string deviceId, string channelId, CancellationToken cancellationToken)
    {
        var subId = Guid.NewGuid().ToString();

        var subscription = await _query.SubscribeSampleAsync(deviceId, channelId, cancellationToken)
            .ConfigureAwait(false);

        var entry = new Entry(deviceId, channelId, subscription, _options.Value.SampleCacheMaxFrames, _timeProvider);

        if (!_entries.TryAdd(subId, entry))
        {
            // GUID 冲突在实践中不会发生；稳妥起见释放刚建立的订阅
            await subscription.UnsubscribeAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException("生成 sub_id 时发生冲突，请重试。");
        }

        subscription.FrameReceived += entry.OnFrame;
        ApiServerLog.LogSubscribed(_logger, subId, deviceId, channelId);
        return subId;
    }

    /// <summary>
    /// 取走某订阅已缓存的帧并清空（drain）。
    ///
    /// 返回值的形状与官方一致：帧数组
    /// <c>[ { "id":通道id, "beginTime":..., "data":[ {"data":[...]} ] } ]</c>。
    /// 订阅不存在时返回 <c>null</c>（由端点转成 FAILED）。
    /// </summary>
    public IReadOnlyList<object>? Drain(string subId)
    {
        if (!_entries.TryGetValue(subId, out var entry)) return null;

        entry.Touch();
        var frames = entry.Drain();
        ApiServerLog.LogDrained(_logger, subId, frames.Count);
        return frames;
    }

    /// <summary>取消订阅并移除缓存。</summary>
    public async Task<bool> UnsubscribeAsync(string subId, CancellationToken cancellationToken)
    {
        if (!_entries.TryRemove(subId, out var entry)) return false;

        entry.Subscription.FrameReceived -= entry.OnFrame;
        await entry.Subscription.UnsubscribeAsync(cancellationToken).ConfigureAwait(false);
        ApiServerLog.LogUnsubscribed(_logger, subId);
        return true;
    }

    /// <summary>空闲条目回收循环。</summary>
    private async Task ReapLoopAsync()
    {
        var interval = TimeSpan.FromMilliseconds(Math.Max(1000, _options.Value.SampleCacheIdleTimeoutMs / 2));
        using var timer = new PeriodicTimer(interval);

        try
        {
            while (await timer.WaitForNextTickAsync(_lifetime.Token).ConfigureAwait(false))
            {
                var idle = TimeSpan.FromMilliseconds(_options.Value.SampleCacheIdleTimeoutMs);
                var now = _timeProvider.GetUtcNow();

                foreach (var (subId, entry) in _entries)
                {
                    if (now - entry.LastAccess < idle) continue;
                    if (!_entries.TryRemove(subId, out var removed)) continue;

                    removed.Subscription.FrameReceived -= removed.OnFrame;
                    try { await removed.Subscription.UnsubscribeAsync(_lifetime.Token).ConfigureAwait(false); }
                    catch (Exception) { /* 回收阶段尽力而为 */ }

                    ApiServerLog.LogReaped(_logger, subId, removed.DeviceId, removed.ChannelId);
                }
            }
        }
        catch (OperationCanceledException) { /* 正常停止 */ }
        catch (Exception e) { ApiServerLog.LogReaperFailed(_logger, e); }
    }

    public async ValueTask DisposeAsync()
    {
        try { await _lifetime.CancelAsync().ConfigureAwait(false); } catch (Exception) { }
        try { await _reaper.ConfigureAwait(false); } catch (Exception) { }

        foreach (var (subId, entry) in _entries)
        {
            entry.Subscription.FrameReceived -= entry.OnFrame;
            try { await entry.Subscription.UnsubscribeAsync().ConfigureAwait(false); }
            catch (Exception) { /* 释放阶段尽力而为 */ }
            _entries.TryRemove(subId, out _);
        }

        _lifetime.Dispose();
    }

    /// <summary>
    /// 一个订阅的缓存条目。帧按到达顺序入队，超出上限丢最旧的。
    ///
    /// 帧被序列化成<b>官方形状的</b> <see cref="JsonElement"/>——
    /// 由库的 <see cref="SampleFrame"/> 转回线上格式，保证与设备发出的报文一致。
    /// </summary>
    private sealed class Entry
    {
        private readonly Queue<JsonElement> _frames = new();
        private readonly Lock _lock = new();
        private readonly int _maxFrames;
        private readonly TimeProvider _time;

        public Entry(string deviceId, string channelId,
            ISampleSubscription subscription, int maxFrames, TimeProvider time)
        {
            DeviceId = deviceId;
            ChannelId = channelId;
            Subscription = subscription;
            _maxFrames = Math.Max(1, maxFrames);
            _time = time;
            LastAccess = time.GetUtcNow();
        }

        public string DeviceId { get; }

        public string ChannelId { get; }

        public ISampleSubscription Subscription { get; }

        public DateTimeOffset LastAccess { get; private set; }

        public void Touch() => LastAccess = _time.GetUtcNow();

        public void OnFrame(object? sender, SampleFrame frame)
        {
            var json = ToWireFrame(frame);
            lock (_lock)
            {
                LastAccess = _time.GetUtcNow();
                while (_frames.Count >= _maxFrames) _frames.Dequeue();
                _frames.Enqueue(json);
            }
        }

        public List<object> Drain()
        {
            lock (_lock)
            {
                var result = new List<object>(_frames.Count);
                while (_frames.Count > 0) result.Add(_frames.Dequeue());
                return result;
            }
        }

        /// <summary>
        /// 把库的 <see cref="SampleFrame"/> 还原成线上形状：
        /// <c>{ "id":通道id, "beginTime":..., "data":[ {"data":[...]} ] }</c>。
        ///
        /// 注意 <c>data[]</c> 元素<b>不带 id</b>——与设备发出的报文保持一致，
        /// 调用方按位置与模型里该通道的 <c>ids</c> 对应。
        /// </summary>
        private static JsonElement ToWireFrame(SampleFrame frame)
        {
            var items = new List<object>(frame.Items.Count);
            foreach (var item in frame.Items)
            {
                var entry = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["data"] = item.Data
                };
                // 设备实测不返回 id；若某个实现返回了就原样带上
                if (!string.IsNullOrEmpty(item.Id)) entry["id"] = item.Id;
                if (!string.IsNullOrEmpty(item.Encoding)) entry["encoding"] = item.Encoding;
                items.Add(entry);
            }

            var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = frame.ChannelId,
                // beginTime 在线上是字符串：设备原始报文形如
                // {"beginTime":"1791349104216"}，官方 apiServer 也原样输出字符串。
                // 库内是 long? 便于计算，这里还原成字符串以免形状不一致。
                ["beginTime"] = frame.BeginTime?.ToString(CultureInfo.InvariantCulture),
                ["data"] = items.Count > 0 ? items : null
            };

            return JsonSerializer.SerializeToElement(payload);
        }
    }
}

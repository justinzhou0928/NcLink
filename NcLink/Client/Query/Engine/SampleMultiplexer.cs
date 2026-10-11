using System.Collections.Concurrent;
using System.Text.Json;
using NcLink.Protocol.Interfaces;
using NcLink.Protocol.Models;
using NcLink.Protocol.Engine;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NcLink.Logging;
using NcLink.Client.Devices;
using NcLink.Client.Query.Interfaces;
using NcLink.Client.Query.Models;

namespace NcLink.Client.Query.Engine;

/// <summary>
/// 采样分发器：把主题 <c>Sample/{deviceId}/{channelId}</c> 上的采样帧
/// 路由到对应的订阅者。
///
/// <b>归属是确定的</b>：主题里带设备 SN（实测官方设备确认），
/// 因此按 (设备, 通道) 精确匹配即可，<b>不需要任何"通道 id 歧义"启发式</b>。
///
/// 曾有一版设计假设"主题只有通道 id、没有 SN"，并据此在通道 id 冲突时丢弃整帧——
/// 那个前提是错的，且导致订阅后永远收不到帧。见
/// <c>docs/official-simulator-verified-protocol.md</c>。
/// </summary>
internal sealed partial class SampleMultiplexer
{
    private readonly INcLinkCodec _codec;
    private readonly DeviceModelCache _models;
    private readonly TimeProvider _time;
    private readonly ILogger<SampleMultiplexer> _logger;

    /// <summary>设备 + 通道 → 订阅（键为 "设备|通道"）。</summary>
    private readonly ConcurrentDictionary<string, SampleSubscription> _subscriptions = new(StringComparer.Ordinal);

    public SampleMultiplexer(
        INcLinkCodec codec, DeviceModelCache models, TimeProvider time, ILogger<SampleMultiplexer> logger)
    {
        _codec = codec ?? throw new ArgumentNullException(nameof(codec));
        _models = models ?? throw new ArgumentNullException(nameof(models));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>当前订阅数。</summary>
    public int SubscriptionCount => _subscriptions.Count;

    /// <summary>注册一个订阅。</summary>
    public SampleSubscription Register(string deviceId, string channelId, Func<Task> unsubscribe)
    {
        var subscription = new SampleSubscription(deviceId, channelId, unsubscribe);

        if (!_subscriptions.TryAdd(Key(deviceId, channelId), subscription))
            return _subscriptions[Key(deviceId, channelId)];   // 已存在，复用

        return subscription;
    }

    /// <summary>注销订阅。</summary>
    public void Unregister(SampleSubscription subscription)
        => _subscriptions.TryRemove(Key(subscription.DeviceId, subscription.ChannelId), out _);

    /// <summary>查找已有订阅（供重复订阅时复用）。</summary>
    public SampleSubscription? Find(string deviceId, string channelId)
        => _subscriptions.TryGetValue(Key(deviceId, channelId), out var existing) ? existing : null;

    /// <summary>
    /// 处理一条采样报文。返回是否成功归属并分发。
    /// </summary>
    public bool TryDispatch(RawMessage message)
    {
        if (!message.Topic.StartsWith(NcLinkTopics.SamplePrefix, StringComparison.Ordinal))
            return false;

        if (!TryParseSampleTopic(message.Topic, out var deviceId, out var topicChannelId))
        {
            SamplingLog.LogMalformedSampleTopic(_logger, message.Topic);
            return false;
        }

        var sample = _codec.Decode<SampleMessage>(message.Payload);
        if (sample is null) return false;

        // 报文体里的 id 是通道 id；与主题里的通道 id 以报文为准（两者实测一致）
        var channelId = string.IsNullOrEmpty(sample.Id) ? topicChannelId : sample.Id!;

        if (Find(deviceId, channelId) is not { } target) return false;

        var items = new List<SampleItem>();
        foreach (var item in sample.Data ?? [])
            items.Add(new SampleItem(item.Id, item.Encoding, item.Data ?? []));

        // 通道配置里的数据项 id，顺序与 items 对应（用于按 id 取值）
        var itemIds = _models.GetChannelItemIds(deviceId, channelId);

        target.Publish(new SampleFrame(
                deviceId, channelId, sample.BeginTime, items, itemIds, _time.GetUtcNow()));
        return true;
    }

    /// <summary>
    /// 从 <c>Sample/{deviceId}/{channelId}</c> 拆出设备与通道。
    ///
    /// 主题固定三层；通道 id 本身可能含 <c>/</c>（模型里出现过空 id 之类的情况），
    /// 因此只在<b>第一个</b>分隔符处切分。
    /// </summary>
    internal static bool TryParseSampleTopic(string topic, out string deviceId, out string channelId)
    {
        deviceId = string.Empty;
        channelId = string.Empty;

        if (!topic.StartsWith(NcLinkTopics.SamplePrefix, StringComparison.Ordinal))
            return false;

        var rest = topic[NcLinkTopics.SamplePrefix.Length..];
        var slash = rest.IndexOf('/');
        if (slash <= 0 || slash == rest.Length - 1) return false;

        deviceId = rest[..slash];
        channelId = rest[(slash + 1)..];
        return deviceId.Length > 0 && channelId.Length > 0;
    }

    private static string Key(string deviceId, string channelId) => deviceId + "|" + channelId;

    /// <summary>全部订阅（释放时用）。</summary>
    public IReadOnlyCollection<SampleSubscription> Subscriptions => _subscriptions.Values.ToArray();

}

/// <summary>采样订阅的默认实现。</summary>
internal sealed class SampleSubscription : ISampleSubscription
{
    private readonly Func<Task> _unsubscribe;
    private long _receivedCount;
    private long _lastReceivedTicks = -1;

    public SampleSubscription(string deviceId, string channelId, Func<Task> unsubscribe)
    {
        DeviceId = deviceId;
        ChannelId = channelId;
        _unsubscribe = unsubscribe;
    }

    public string DeviceId { get; }

    public string ChannelId { get; }

    public long ReceivedCount => Interlocked.Read(ref _receivedCount);

    public DateTimeOffset? LastReceivedAt
    {
        get
        {
            var ticks = Interlocked.Read(ref _lastReceivedTicks);
            return ticks < 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    public event EventHandler<SampleFrame>? FrameReceived;

    internal void Publish(SampleFrame frame)
    {
        Interlocked.Increment(ref _receivedCount);
        Interlocked.Exchange(ref _lastReceivedTicks, frame.ReceivedAt.UtcTicks);

        try
        {
            FrameReceived?.Invoke(this, frame);
        }
        catch (Exception e)
        {
            throw new NcLinkProtocolException($"采样订阅回调（{DeviceId}/{ChannelId}）抛出异常。", innerException: e);
        }
    }

    public Task UnsubscribeAsync(CancellationToken cancellationToken = default) => _unsubscribe();

    public ValueTask DisposeAsync() => new(_unsubscribe());
}

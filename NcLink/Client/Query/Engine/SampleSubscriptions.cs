using NcLink.Protocol.Interfaces;
using NcLink.Client.Connection.Interfaces;
using NcLink.Client.Devices.Engine;
using NcLink.Client.Query.Interfaces;

namespace NcLink.Client.Query.Engine;

/// <summary>
/// 采样订阅清单：持有并管理 <c>Sample/{channelId}</c> 的订阅与注销。
///
/// 归因规则见 <see cref="SampleMultiplexer"/>：通道 id 在多台设备间重复时丢弃该帧，
/// 而不是猜测归属。
/// </summary>
internal sealed class SampleSubscriptions : ISampling
{
    private readonly IRequestPipeline _pipeline;
    private readonly DeviceModelCache _models;
    private readonly SampleMultiplexer _samples;
    private readonly INcLinkTopics _topics;
    private readonly INcLinkTransport _transport;

    public SampleSubscriptions(
        IRequestPipeline pipeline,
        DeviceModelCache models,
        INcLinkTransport transport,
        INcLinkTopics topics,
        SampleMultiplexer samples)
    {
        _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
        _models = models ?? throw new ArgumentNullException(nameof(models));
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _topics = topics ?? throw new ArgumentNullException(nameof(topics));
        _samples = samples ?? throw new ArgumentNullException(nameof(samples));
    }

    /// <summary>内部多路复用器（客户端需要它来分发报文）。</summary>
    public SampleMultiplexer Multiplexer => _samples;

    /// <summary>订阅一个采样通道（重复订阅返回同一句柄）。</summary>
    public async Task<ISampleSubscription> SubscribeAsync(
        string deviceId,
        string channelId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(channelId);

        if (_samples.Find(deviceId, channelId) is { } existing) return existing;

        await _pipeline.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);

        // 主题是 Sample/{deviceId}/{channelId}——必须带 SN，否则收不到帧
        var topic = _topics.Sample(deviceId, channelId);
        await _transport.SubscribeAsync(topic, cancellationToken).ConfigureAwait(false);

        return _samples.Register(deviceId, channelId, async () =>
        {
            if (_samples.Find(deviceId, channelId) is { } current)
                _samples.Unregister(current);

            await _transport.UnsubscribeAsync(topic).ConfigureAwait(false);
        });
    }

    /// <summary>订阅设备模型里的全部采样通道。</summary>
    public async Task<IReadOnlyList<ISampleSubscription>> SubscribeAllAsync(
        string deviceId,
        CancellationToken cancellationToken = default)
    {
        var model = await _models.GetAsync(deviceId, false, null, cancellationToken).ConfigureAwait(false);

        var subscriptions = new List<ISampleSubscription>();
        foreach (var channel in model.SampleChannels.Values)
            subscriptions.Add(await SubscribeAsync(deviceId, channel.ChannelId, cancellationToken)
                .ConfigureAwait(false));

        return subscriptions;
    }

    /// <summary>注销全部订阅（释放时用）。</summary>
    public async Task UnsubscribeAllAsync()
    {
        foreach (var subscription in _samples.Subscriptions)
        {
            try
            {
                await subscription.UnsubscribeAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 释放阶段尽力而为：单个取消失败不应阻断整体释放
            }
        }
    }
}

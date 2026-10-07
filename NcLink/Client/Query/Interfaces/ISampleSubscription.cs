using NcLink.Client.Query.Models;

namespace NcLink.Client.Query.Interfaces;

/// <summary>
/// 采样订阅句柄。
///
/// 由 <c>INcLinkOperations.SubscribeSampleAsync</c> 返回。
/// 数据形状见 <see cref="SampleFrame"/>。
/// </summary>
public interface ISampleSubscription : IAsyncDisposable
{
    /// <summary>订阅所属设备 SN。</summary>
    string DeviceId { get; }

    /// <summary>采样通道 id。</summary>
    string ChannelId { get; }

    /// <summary>已收到的帧数。</summary>
    long ReceivedCount { get; }

    /// <summary>最近一帧的收到时间。</summary>
    DateTimeOffset? LastReceivedAt { get; }

    /// <summary>收到采样帧。</summary>
    event EventHandler<SampleFrame>? FrameReceived;

    /// <summary>取消订阅。</summary>
    Task UnsubscribeAsync(CancellationToken cancellationToken = default);
}

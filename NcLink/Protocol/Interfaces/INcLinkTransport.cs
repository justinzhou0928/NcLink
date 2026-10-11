using NcLink.Protocol.Models;
namespace NcLink.Protocol.Interfaces;

/// <summary>
/// NC-Link 的传输层抽象：负责连接、订阅、发布与收包。
///
/// 这一层刻意<b>不</b>依赖任何具体 MQTT 库——实现放在
/// <c>NcLink.Transport.Mqtt</c>，测试可用内存假实现替换。
///
/// <b>为何同时要求 <see cref="IDisposable"/> 与 <see cref="IAsyncDisposable"/></b>：
/// 传输作为单例注册在容器里，容器释放实例时
/// <c>ServiceProvider.Dispose()</c>（同步）遇到只实现 <see cref="IAsyncDisposable"/>
/// 的单例会直接抛 <see cref="InvalidOperationException"/>，
/// 而 <c>using var provider = services.BuildServiceProvider()</c> 是很常见的写法。
/// 两者都提供，调用方按自己的场景选（<c>await using</c> 优先）。
/// 释放逻辑本身通常不需要等待网络，转调即可。
/// </summary>
public interface INcLinkTransport : IDisposable, IAsyncDisposable
{
    /// <summary>当前连接状态。</summary>
    NcLinkConnectionState State { get; }

    /// <summary>是否已连接。</summary>
    bool IsConnected { get; }

    /// <summary>连接地址，用于日志与异常信息。</summary>
    string Endpoint { get; }

    /// <summary>建立连接（幂等）。</summary>
    Task ConnectAsync(CancellationToken cancellationToken = default);

    /// <summary>断开连接。</summary>
    Task DisconnectAsync(CancellationToken cancellationToken = default);

    /// <summary>订阅主题（实现应记录并在重连后自动恢复）。</summary>
    Task SubscribeAsync(string topic, CancellationToken cancellationToken = default);

    /// <summary>取消订阅。</summary>
    Task UnsubscribeAsync(string topic, CancellationToken cancellationToken = default);

    /// <summary>发布原始字节。序列化由 <see cref="INcLinkCodec"/> 负责，传输层只搬字节。</summary>
    Task PublishAsync(string topic, byte[] payload, CancellationToken cancellationToken = default);

    /// <summary>收到任意报文时触发。</summary>
    event Func<RawMessage, Task>? MessageReceived;

    /// <summary>连接状态变化时触发。</summary>
    event Action<NcLinkConnectionState>? StateChanged;

    /// <summary>连接丢失时触发（参数为原因，可能为 null）。</summary>
    event Action<Exception?>? ConnectionLost;
}

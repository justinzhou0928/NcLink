using NcLink.Protocol.Contracts;

namespace NcLink.Tests.Fakes;

/// <summary>
/// 内存假传输：让关联器与客户端可以在<b>不需要 MQTT Broker</b> 的情况下被测试。
/// 这正是分出 <see cref="INcLinkTransport"/> 抽象的主要目的。
/// </summary>
public sealed class FakeTransport : INcLinkTransport
{
    private readonly HashSet<string> _subscriptions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, NcLinkEnvelope> _lastRequestsByTopic = new(StringComparer.Ordinal);

    /// <summary>已发布到各主题的最后一条请求（按主题索引）。</summary>
    public IReadOnlyDictionary<string, NcLinkEnvelope> LastRequests => _lastRequestsByTopic;

    /// <summary>发布过的全部主题（按顺序）。</summary>
    public List<string> PublishedTopics { get; } = [];

    /// <summary>已订阅主题。</summary>
    public IReadOnlyCollection<string> Subscriptions => _subscriptions;

    /// <summary>发布请求时的回调；测试在这里注入"设备响应"。</summary>
    public Func<string, byte[], CancellationToken, Task>? OnPublish { get; set; }

    /// <summary>连接时的回调（可用于断言客户端先连接再发请求）。</summary>
    public Func<CancellationToken, Task>? OnConnect { get; set; }

    public NcLinkConnectionState State { get; private set; } = NcLinkConnectionState.Disconnected;

    public bool IsConnected { get; private set; }

    public string Endpoint => "memory://fake";

    public event Func<RawMessage, Task>? MessageReceived;
    public event Action<NcLinkConnectionState>? StateChanged;
    public event Action<Exception?>? ConnectionLost;

    public int ConnectCount { get; private set; }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        ConnectCount++;
        if (OnConnect is not null) await OnConnect(cancellationToken).ConfigureAwait(false);

        IsConnected = true;
        State = NcLinkConnectionState.Connected;
        StateChanged?.Invoke(State);
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        IsConnected = false;
        State = NcLinkConnectionState.Disconnected;
        StateChanged?.Invoke(State);
        return Task.CompletedTask;
    }

    public Task SubscribeAsync(string topic, CancellationToken cancellationToken = default)
    {
        lock (_subscriptions) _subscriptions.Add(topic);
        return Task.CompletedTask;
    }

    public Task UnsubscribeAsync(string topic, CancellationToken cancellationToken = default)
    {
        lock (_subscriptions) _subscriptions.Remove(topic);
        return Task.CompletedTask;
    }

    public async Task PublishAsync(string topic, byte[] payload, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        PublishedTopics.Add(topic);

        if (OnPublish is not null)
            await OnPublish(topic, payload, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>模拟设备向某主题发送报文。</summary>
    public async Task InjectAsync(string topic, string json)
    {
        var handler = MessageReceived;
        if (handler is null) return;

        var message = new RawMessage(topic, System.Text.Encoding.UTF8.GetBytes(json));
        await handler(message).ConfigureAwait(false);
    }

    /// <summary>模拟连接丢失。</summary>
    public void SimulateConnectionLost(Exception? exception = null)
    {
        IsConnected = false;
        State = NcLinkConnectionState.Disconnected;
        StateChanged?.Invoke(State);
        ConnectionLost?.Invoke(exception);
    }

    /// <summary>记录请求对象（便于断言报文内容）。</summary>
    public void RecordRequest(string topic, NcLinkEnvelope request) => _lastRequestsByTopic[topic] = request;

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// 同步释放。契约 <see cref="INcLinkTransport"/> 要求两种释放都提供——
    /// 传输是容器持有的单例，容器<b>同步</b>释放时只认 <see cref="IDisposable"/>。
    /// </summary>
    public void Dispose()
    {
        IsConnected = false;
        State = NcLinkConnectionState.Disconnected;
    }
}

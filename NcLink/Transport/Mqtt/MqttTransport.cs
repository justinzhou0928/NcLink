using System.Collections.Concurrent;
using System.Text;
using MQTTnet;
using MQTTnet.Protocol;
using NcLink.Protocol.Interfaces;
using NcLink.Protocol.Models;
using NcLink.Client.Connection;

namespace NcLink.Transport.Mqtt;

/// <summary>
/// 基于 MQTTnet 的 NC-Link 传输实现。
///
/// 这是整个解决方案中<b>唯一</b>依赖 MQTTnet 的项目；其它项目只依赖
/// <see cref="INcLinkTransport"/> 抽象，因此可以在测试中用内存假实现替换。
///
/// 说明：MQTTnet 5 不再提供自动重连选项，重连逻辑在此自行实现，
/// 并在重连成功后自动恢复已注册的订阅。
///
/// 推荐用 <c>services.AddNcLinkMqtt(...)</c> 注册，由容器注入
/// <see cref="Func{IMqttClient}"/> 与 <see cref="MqttTransportOptions"/>。
/// </summary>
public sealed class MqttTransport : INcLinkTransport
{
    private readonly MqttTransportOptions _options;
    private readonly Func<IMqttClient> _clientFactory;
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private readonly ConcurrentDictionary<string, byte> _subscriptions = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _lifetime = new();

    private IMqttClient? _client;
    private Task? _reconnectLoop;
    private bool _disposed;

    /// <summary>
    /// 便捷构造：使用 MQTTnet 的默认客户端工厂，且<b>不校验</b>配置。
    ///
    /// 生产代码请用 <c>AddNcLinkMqtt(...)</c>（带 <c>ValidateOnStart</c>）。
    /// </summary>
    public MqttTransport(MqttTransportOptions options)
        : this(options, () => new MqttClientFactory().CreateMqttClient())
    {
    }

    /// <summary>
    /// 由容器解析。构造函数参数一律必填——依赖在签名上可见。
    /// </summary>
    /// <param name="options">MQTT 连接设置（构造前应已通过 <c>Validate()</c>）。</param>
    /// <param name="clientFactory">
    /// 创建 MQTT 客户端。MQTTnet 5 没有 <c>IMqttClientFactory</c> 接口，
    /// 因此用工厂委托做注入点——便于替换与测试，且不必为此自造接口。
    /// </param>
    public MqttTransport(MqttTransportOptions options, Func<IMqttClient> clientFactory)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
    }

    /// <inheritdoc />
    public string Endpoint => _options.BrokerAddress;

    /// <inheritdoc />
    public NcLinkConnectionState State { get; private set; } = NcLinkConnectionState.Disconnected;

    /// <inheritdoc />
    public bool IsConnected => _client?.IsConnected == true;

    /// <inheritdoc />
    public event Func<RawMessage, Task>? MessageReceived;

    /// <inheritdoc />
    public event Action<NcLinkConnectionState>? StateChanged;

    /// <inheritdoc />
    public event Action<Exception?>? ConnectionLost;

    /// <inheritdoc />
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        await _connectLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsConnected) return;

            SetState(NcLinkConnectionState.Connecting);

            _client ??= CreateClient();

            var options = BuildClientOptions();
            await _client.ConnectAsync(options, cancellationToken).ConfigureAwait(false);

            SetState(NcLinkConnectionState.Connected);

            // 重放订阅，保证重连后行为一致。Keys 本身就是快照，无需额外加锁
            string[] topics = [.. _subscriptions.Keys];

            foreach (var topic in topics)
                await SubscribeCoreAsync(topic, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _connectLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        if (_client?.IsConnected != true) return;

        try
        {
            await _client.DisconnectAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 断开阶段的异常不向上抛：调用方关心的是"最终没连上"
        }

        SetState(NcLinkConnectionState.Disconnected);
    }

    /// <inheritdoc />
    public async Task SubscribeAsync(string topic, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(topic);

        _subscriptions.TryAdd(topic, 0);

        if (IsConnected)
            await SubscribeCoreAsync(topic, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task UnsubscribeAsync(string topic, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(topic);

        _subscriptions.TryRemove(topic, out _);

        if (_client?.IsConnected != true) return;

        try
        {
            await _client.UnsubscribeAsync(topic, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 断线期间取消订阅失败可以忽略：重连后不会再订阅它
        }
    }

    /// <inheritdoc />
    public async Task PublishAsync(string topic, byte[] payload, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(topic);
        ArgumentNullException.ThrowIfNull(payload);

        if (_client?.IsConnected != true)
            throw new NcLinkNotConnectedException(_options.BrokerAddress);

        var message = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .WithQualityOfServiceLevel(_options.PublishQos)
            .Build();

        await _client.PublishAsync(message, cancellationToken).ConfigureAwait(false);
    }

    #region 内部

    private IMqttClient CreateClient()
    {
        var client = _clientFactory();

        client.ApplicationMessageReceivedAsync += OnMessageAsync;
        client.ConnectedAsync += OnConnectedAsync;
        client.DisconnectedAsync += OnDisconnectedAsync;

        return client;
    }

    private MqttClientOptions BuildClientOptions()
    {
        var (host, port) = ParseEndpoint(_options.BrokerAddress);

        var builder = new MqttClientOptionsBuilder()
            .WithTcpServer(host, port)
            .WithClientId(_options.ClientId ?? $"nclink-{Guid.NewGuid():N}"[..28])
            .WithCleanSession(_options.CleanSession)
            .WithTimeout(_options.ConnectTimeout)
            .WithKeepAlivePeriod(_options.KeepAlivePeriod);

        if (!string.IsNullOrEmpty(_options.UserName))
            builder = builder.WithCredentials(_options.UserName, _options.Password);

        return builder.Build();
    }

    private async Task SubscribeCoreAsync(string topic, CancellationToken cancellationToken)
    {
        if (_client?.IsConnected != true) return;

        var options = new MqttClientSubscribeOptionsBuilder()
            .WithTopicFilter(f => f
                .WithTopic(topic)
                .WithQualityOfServiceLevel(_options.SubscriptionQos))
            .Build();

        await _client.SubscribeAsync(options, cancellationToken).ConfigureAwait(false);
    }

    private async Task OnMessageAsync(MqttApplicationMessageReceivedEventArgs args)
    {
        var handler = MessageReceived;
        if (handler is null) return;

        // MQTTnet 5 的 Payload 是 ReadOnlySequence<byte>，用 BuffersExtensions.ToArray 显式转换
        var payload = args.ApplicationMessage.Payload;
        var bytes = payload.Length > 0
            ? System.Buffers.BuffersExtensions.ToArray(payload)
            : [];

        var message = new RawMessage(args.ApplicationMessage.Topic, bytes);

        try
        {
            await handler(message).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 单个订阅者的异常不应影响 MQTT 回调线程
        }
    }

    private Task OnConnectedAsync(MqttClientConnectedEventArgs args)
    {
        SetState(NcLinkConnectionState.Connected);
        return Task.CompletedTask;
    }

    private Task OnDisconnectedAsync(MqttClientDisconnectedEventArgs args)
    {
        SetState(NcLinkConnectionState.Disconnected);
        ConnectionLost?.Invoke(args.Exception);

        if (_options.AutoReconnect && !_disposed && !_lifetime.IsCancellationRequested)
            _reconnectLoop ??= Task.Run(() => ReconnectLoopAsync(_lifetime.Token));

        return Task.CompletedTask;
    }

    private async Task ReconnectLoopAsync(CancellationToken cancellationToken)
    {
        var delay = _options.ReconnectInitialDelay;

        while (!cancellationToken.IsCancellationRequested && !IsConnected)
        {
            try
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                await ConnectAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception)
            {
                var next = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, _options.ReconnectMaxDelay.Ticks));
                delay = next;
            }
        }
    }

    private void SetState(NcLinkConnectionState state)
    {
        if (State == state) return;
        State = state;
        StateChanged?.Invoke(state);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(MqttTransport));
    }

    /// <summary>解析 <c>tcp://host:port</c>；缺省端口 1883。</summary>
    internal static (string Host, int Port) ParseEndpoint(string address)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);

        var value = address;
        var schemeIndex = value.IndexOf("://", StringComparison.Ordinal);
        if (schemeIndex >= 0) value = value[(schemeIndex + 3)..];

        var colonIndex = value.LastIndexOf(':');
        if (colonIndex > 0 && int.TryParse(value[(colonIndex + 1)..], out var port))
            return (value[..colonIndex], port);

        return (value, 1883);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        try { await _lifetime.CancelAsync().ConfigureAwait(false); }
        catch (Exception) { /* 已释放 */ }

        if (_reconnectLoop is not null)
        {
            try { await _reconnectLoop.ConfigureAwait(false); }
            catch (Exception) { /* 重连循环退出 */ }
        }

        if (_client is not null)
        {
            _client.ApplicationMessageReceivedAsync -= OnMessageAsync;
            _client.ConnectedAsync -= OnConnectedAsync;
            _client.DisconnectedAsync -= OnDisconnectedAsync;

            try
            {
                if (_client.IsConnected)
                    await _client.DisconnectAsync().ConfigureAwait(false);
            }
            catch (Exception) { /* 释放阶段忽略 */ }

            _client.Dispose();
            _client = null;
        }

        _lifetime.Dispose();
        _connectLock.Dispose();
        SetState(NcLinkConnectionState.Disconnected);
    }

    /// <summary>
    /// 同步释放。取消并等待重连循环，然后断开并释放 MQTT 客户端。
    ///
    /// 提供它是因为 <c>ServiceProvider.Dispose()</c>（同步释放）遇到只实现
    /// <see cref="IAsyncDisposable"/> 的单例会直接抛异常，
    /// 而 <c>using var provider = services.BuildServiceProvider()</c>
    /// 是很常见的写法。等待重连循环不会死锁——它的循环体带取消令牌调用
    /// <c>Task.Delay</c>，取消后立即完成。
    /// </summary>
    public void Dispose()
        => DisposeAsync().AsTask().GetAwaiter().GetResult();

    #endregion

}

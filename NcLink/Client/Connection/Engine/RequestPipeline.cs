using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NcLink.Protocol;
using NcLink.Protocol.Contracts;
using NcLink.General.Logging;
using NcLink.Client.Connection.Options;
using NcLink.Client.Connection.Interfaces;

namespace NcLink.Client.Connection.Engine;

/// <summary><see cref="IRequestPipeline"/> 的默认实现。</summary>
internal sealed partial class RequestPipeline : IRequestPipeline, IDisposable
{
    private readonly INcLinkTransport _transport;
    private readonly INcLinkTopics _topics;
    private readonly NcLinkClientOptions _options;
    private readonly RequestCorrelator _correlator;
    private readonly ILogger<RequestPipeline> _logger;

    private int _initialized;
    private Func<RawMessage, Task>? _onMessage;

    public RequestPipeline(
        INcLinkTransport transport,
        INcLinkCodec codec,
        INcLinkTopics topics,
        NcLinkClientOptions options,
        CorrelationOptions correlationOptions,
        ILoggerFactory loggerFactory,
        TimeProvider? timeProvider = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _topics = topics ?? throw new ArgumentNullException(nameof(topics));
        _options = options ?? throw new ArgumentNullException(nameof(options));

        ArgumentNullException.ThrowIfNull(loggerFactory);
        _logger = loggerFactory.CreateLogger<RequestPipeline>();

        _correlator = new RequestCorrelator(
            _transport,
            codec ?? throw new ArgumentNullException(nameof(codec)),
            _topics,
            correlationOptions ?? throw new ArgumentNullException(nameof(correlationOptions)),
            defaultTimeout: options.RequestTimeout,
            timeProvider: timeProvider ?? TimeProvider.System,
            logger: loggerFactory.CreateLogger<RequestCorrelator>());
    }

    public NcLinkConnectionState ConnectionState => _transport.State;

    /// <summary>底层传输（释放时需要）。</summary>
    public INcLinkTransport Transport => _transport;

    /// <summary>关联器（释放时需要 Detach）。</summary>
    public RequestCorrelator Correlator => _correlator;

    public async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _initialized, 1) == 1 && _transport.IsConnected) return;

        PipelineLog.LogConnecting(_logger, _transport.Endpoint);
        await _transport.ConnectAsync(cancellationToken).ConfigureAwait(false);
        PipelineLog.LogConnected(_logger, _transport.Endpoint);

        // 订阅全部响应主题通配符：一个连接服务多台设备时必需，
        // 否则收不到未经单独订阅的主题上的响应。
        foreach (var filter in _topics.AllResponseFilters)
            await _transport.SubscribeAsync(filter, cancellationToken).ConfigureAwait(false);

        PipelineLog.LogSubscribedAllResponseTopics(_logger, _topics.AllResponseFilters.Count);
    }

    public async Task<RawMessage> SendAsync(
        string topic,
        RequestContext context,
        NcLinkEnvelope request,
        Func<RequestContext, bool> matcher,
        TimeSpan? timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(matcher);

        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);

        var requestContext = new RequestContext(topic, correlationId: string.Empty, context.DeviceId, context.ItemIds);

        // 严格与降级两条路径都要求语义校验通过：
        // @id 的归属判定由关联器负责；只有设备没回显 @id 时才走降级匹配。
        return await _correlator.SendAsync(
            request,
            requestContext,
            strictMatcher: matcher,
            fallbackMatcher: matcher,
            timeout: timeout,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_onMessage is not null)
            _transport.MessageReceived -= _onMessage;

        _correlator.Detach();
    }

    /// <summary>采样报文转发（由客户端在构造后接入）。</summary>
    internal void ForwardSamplesTo(Action<RawMessage> dispatch)
    {
        ArgumentNullException.ThrowIfNull(dispatch);

        _onMessage = message =>
        {
            if (message.Topic.StartsWith(NcLinkTopics.SamplePrefix, StringComparison.Ordinal))
                dispatch(message);

            return Task.CompletedTask;
        };

        _transport.MessageReceived += _onMessage;
    }

}

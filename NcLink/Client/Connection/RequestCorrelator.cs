using System.Collections.Concurrent;
using NcLink.Protocol.Interfaces;
using NcLink.Protocol.Models;
using NcLink.Protocol.Engine;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NcLink.Logging;
using NcLink.Client.Connection;

namespace NcLink.Client.Connection;

/// <summary>
/// 请求-响应关联器的默认实现。
///
/// 匹配策略（两层，顺序不可颠倒）：
/// <list type="number">
/// <item><b>严格匹配</b>：报文体含本请求的关联 id（<c>@id</c>），或调用方给出的强特征；</item>
/// <item><b>降级匹配</b>：仅在没有任何严格匹配命中时启用，
/// 且必须先通过 <see cref="INcLinkTopics.IsResponseFor"/> 校验响应主题归属，
/// 再看报文体是否包含请求的数据项 id。</item>
/// </list>
///
/// 之所以强调顺序与主题校验：若只按「报文里出现设备 SN」之类的弱特征兜底，
/// 设备掉线时的<b>迟到响应</b>会被后续请求领走，表现为「已掉线的设备仍返回成功」。
/// </summary>
internal sealed partial class RequestCorrelator
{
    private readonly INcLinkTransport _transport;
    private readonly INcLinkCodec _codec;
    private readonly INcLinkTopics _topics;
    private readonly CorrelationOptions _options;
    private readonly TimeSpan _defaultTimeout;
    private readonly ILogger<RequestCorrelator> _logger;

    private readonly ConcurrentDictionary<string, PendingRequest> _pending = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _deviceActivity = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// 关联 id 的全局序号。
    ///
    /// 必须是<b>全局</b>而非实例级：实例级计数器会让不同客户端（甚至同一进程内不同测试用例）
    /// 产生相同的 <c>@id</c>，而响应匹配是按 <c>"@id":"&lt;id&gt;"</c> 文本比对的，
    /// 于是可能认领到别的客户端实例的响应。
    /// </summary>
    private static long _globalSequence;

    /// <summary>本实例的标识，与全局序号一起保证关联 id 跨实例唯一且简短。</summary>
    private readonly string _instanceId = Guid.NewGuid().ToString("N")[..8];

    /// <param name="options">关联语义设置（活动窗口）。</param>
    /// <param name="defaultTimeout">
    /// 默认请求超时。来源是 <c>NcLinkClientOptions.RequestTimeout</c>——
    /// 超时只在这一处配置，不放进 <see cref="CorrelationOptions"/>，避免两处不一致。
    /// </param>
    public RequestCorrelator(
        INcLinkTransport transport,
        INcLinkCodec codec,
        INcLinkTopics topics,
        CorrelationOptions options,
        TimeSpan defaultTimeout,
        TimeProvider timeProvider,
        ILogger<RequestCorrelator> logger)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _codec = codec ?? throw new ArgumentNullException(nameof(codec));
        _topics = topics ?? throw new ArgumentNullException(nameof(topics));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _defaultTimeout = defaultTimeout > TimeSpan.Zero
            ? defaultTimeout
            : throw new ArgumentOutOfRangeException(nameof(defaultTimeout), defaultTimeout, "默认超时必须为正。");
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _transport.MessageReceived += OnMessageAsync;
    }

    /// <summary>生成下一次请求的关联 id。</summary>
    private string NextCorrelationId()
        => _instanceId + "-" + Interlocked.Increment(ref _globalSequence);

    /// <summary>当前在途请求数（诊断用）。</summary>
    public int PendingCount => _pending.Count;

    /// <summary>设备是否在活动窗口内出现过。</summary>
    public bool IsDeviceActive(string deviceId)
        => _deviceActivity.TryGetValue(deviceId, out var at)
           && _timeProvider.GetUtcNow() - at <= _options.ActivityWindow;

    /// <summary>
    /// 发送请求并等待响应。
    /// </summary>
    /// <param name="request">请求报文（其 <c>CorrelationId</c> 会被填写）。</param>
    /// <param name="context">
    /// 匹配上下文：关联器派生一份并回填响应文本与 <c>@id</c> 命中标记，
    /// 匹配器通过 <paramref name="strictMatcher"/> / <paramref name="fallbackMatcher"/> 读取。
    /// </param>
    /// <param name="strictMatcher">
    /// 严格匹配时附加的语义校验（可选）。当 <c>@id</c> 命中且响应主题归属正确时调用；
    /// 未提供则以 <c>@id</c> 为准。
    /// </param>
    /// <param name="fallbackMatcher">
    /// 降级匹配：仅当没有任何严格匹配命中时才使用，且仅在响应<b>未携带</b> <c>@id</c> 时生效
    /// （携带了不匹配的 <c>@id</c> 说明该响应属于其它请求，绝不可认领）。
    /// </param>
    /// <param name="timeout">超时时间；为 null 时用默认值。</param>
    public async Task<RawMessage> SendAsync(
        NcLinkEnvelope request,
        RequestContext context,
        Func<RequestContext, bool>? strictMatcher = null,
        Func<RequestContext, bool>? fallbackMatcher = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        if (!_transport.IsConnected)
            await _transport.ConnectAsync(cancellationToken).ConfigureAwait(false);

        var correlationId = NextCorrelationId();

        // 每次发送都指派新的关联 id：复用同一个请求对象时，
        // 沿用旧 id 会让两个在途请求互相认领响应。
        request.CorrelationId = correlationId;

        var effectiveTimeout = timeout ?? _defaultTimeout;
        var completion = new TaskCompletionSource<RawMessage>(TaskCreationOptions.RunContinuationsAsynchronously);

        _pending[correlationId] = new PendingRequest(correlationId, context, completion, strictMatcher, fallbackMatcher);

        // 刻意不在此订阅响应主题：那是连接生命周期的职责，
        // 由 RequestPipeline 连接时一次性订阅通配符完成。理由见 AGENTS.md §2.5。
        try
        {
            // 传输层只搬字节，序列化在这里完成
            var payload = _codec.Encode(request);
            await _transport.PublishAsync(context.Topic, payload, cancellationToken).ConfigureAwait(false);

            CorrelationLog.LogRequestSent(_logger, context.DeviceId, context.Topic, correlationId, effectiveTimeout);

            // Task.WaitAsync 同时给出超时与取消：超时抛 TimeoutException、令牌取消抛
            // OperationCanceledException。此前手工用 CreateLinkedTokenSource + CancelAfter
            // + Register 拼同一件事——每次请求多分配一个 CTS 与一个取消注册，
            // 还要靠 when 过滤器区分"到底是谁取消的"。BCL 直接有。
            try
            {
                return await completion.Task.WaitAsync(effectiveTimeout, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                throw CreateTimeoutException(context, effectiveTimeout);
            }
        }
        finally
        {
            _pending.TryRemove(correlationId, out _);
        }
    }

    /// <summary>区分「超时」与「设备不在线」，便于上层给出可操作的错误信息。</summary>
    private NcLinkException CreateTimeoutException(RequestContext context, TimeSpan timeout)
    {
        if (!string.IsNullOrEmpty(context.DeviceId) && !IsDeviceActive(context.DeviceId))
        {
            CorrelationLog.LogDeviceNeverSeen(_logger, context.DeviceId, context.Topic);
            return new NcLinkDeviceUnavailableException(context.DeviceId);
        }

        CorrelationLog.LogRequestTimedOut(_logger, context.DeviceId, context.Topic, timeout, _pending.Count);
        return new NcLinkTimeoutException(context.Topic, timeout);
    }

    private Task OnMessageAsync(RawMessage message)
    {
        // 记录设备活动时间（响应主题尾部即设备 SN）
        var deviceId = DeviceIdFromTopic(message.Topic);
        if (deviceId is not null)
            _deviceActivity[deviceId] = _timeProvider.GetUtcNow();

        if (_pending.IsEmpty)
        {
            CorrelationLog.LogUnmatchedResponse(_logger, message.Topic, "没有在途请求");
            return Task.CompletedTask;
        }

        // 响应按字节参与匹配，不解码成字符串——绝大多数消息不匹配任何在途请求，
        // 解码整条报文等于白分配一份 UTF-16 副本。理由见 RequestContext 的类注释。
        var payload = message.Payload.AsMemory();

        // 第一遍：严格匹配
        foreach (var entry in _pending)
        {
            if (entry.Value.TryMatchStrict(message.Topic, payload, _topics))
            {
                entry.Value.Completion.TrySetResult(message);
                _pending.TryRemove(entry.Key, out _);
                CorrelationLog.LogResponseMatched(_logger, message.Topic, entry.Key, "严格（@id 命中）");
                return Task.CompletedTask;
            }
        }

        // 第二遍：降级匹配（仅当无任何严格匹配命中）
        foreach (var entry in _pending)
        {
            if (entry.Value.TryMatchFallback(message.Topic, payload, _topics))
            {
                entry.Value.Completion.TrySetResult(message);
                _pending.TryRemove(entry.Key, out _);
                CorrelationLog.LogResponseMatched(_logger, message.Topic, entry.Key, "降级（设备未回显 @id）");
                return Task.CompletedTask;
            }
        }

        // 有在途请求但没人认领：说明这条响应不属于任何等待中的请求。
        // 这正是"迟到响应"的形态，必须记下来——它以前被误领过，导致过偶发故障。
        CorrelationLog.LogUnmatchedResponse(_logger, message.Topic, $"在途请求 {_pending.Count} 个，均不匹配");
        return Task.CompletedTask;
    }

    /// <summary>从响应主题尾部提取设备 SN。</summary>
    private static string? DeviceIdFromTopic(string topic)
    {
        foreach (var prefix in new[]
                 {
                     NcLinkTopics.QueryResponsePrefix,
                     NcLinkTopics.SetResponsePrefix,
                     NcLinkTopics.ProbeQueryResponsePrefix,
                     NcLinkTopics.ProbeSetResponsePrefix
                 })
        {
            if (topic.StartsWith(prefix, StringComparison.Ordinal))
                return topic[prefix.Length..];
        }
        return null;
    }

    /// <summary>解除对传输层的订阅（释放时调用）。</summary>
    public void Detach() => _transport.MessageReceived -= OnMessageAsync;

    #region 待处理请求

    private sealed class PendingRequest
    {
        private readonly RequestContext _context;
        private readonly Func<RequestContext, bool>? _strictMatcher;

        public PendingRequest(
            string correlationId,
            RequestContext context,
            TaskCompletionSource<RawMessage> completion,
            Func<RequestContext, bool>? strictMatcher,
            Func<RequestContext, bool>? fallbackMatcher)
        {
            CorrelationId = correlationId;
            _context = context;
            Completion = completion;
            _strictMatcher = strictMatcher;
            FallbackMatcher = fallbackMatcher;
        }

        public string CorrelationId { get; }

        public TaskCompletionSource<RawMessage> Completion { get; }

        public Func<RequestContext, bool>? FallbackMatcher { get; }

        public bool TryMatchStrict(string topic, ReadOnlyMemory<byte> payload, INcLinkTopics topics)
        {
            // 关联 id 必须作为 JSON 值出现，而不是随便一处子串。
            // 用裸子串匹配会误命中报文里的其它数字/字符串（例如 "id":"01035430" 里含 "1"）。
            if (!ContainsCorrelationId(payload.Span)) return false;

            // 即使 @id 命中，也必须确认这条响应属于本次请求的主题：
            // 否则同一 Broker 上另一个客户端实例的响应（其 @id 恰好相同）会被误领。
            if (!topics.IsResponseFor(topic, _context.Topic, _context.DeviceId)) return false;

            // 可选：调用方给出更严格的语义校验。
            // 未提供时以 @id 为准（关联 id 全局唯一，命中即唯一对应）。
            var context = _context.For(payload);
            context.CorrelationMatched = true;
            return _strictMatcher?.Invoke(context) ?? true;
        }

        /// <summary>报文体里的 <c>@id</c> 是否等于本次请求的关联 id。</summary>
        private bool ContainsCorrelationId(ReadOnlySpan<byte> payload)
        {
            var marker = "\"@id\":\""u8;
            var index = payload.IndexOf(marker);
            if (index < 0) return false;

            var rest = payload[(index + marker.Length)..];
            if (rest.Length < CorrelationId.Length + 1) return false;

            // 关联 id 由 ASCII 组成，逐字节比对即可
            for (var i = 0; i < CorrelationId.Length; i++)
                if (rest[i] != (byte)CorrelationId[i]) return false;

            return rest[CorrelationId.Length] == (byte)'"';
        }

        public bool TryMatchFallback(string topic, ReadOnlyMemory<byte> payload, INcLinkTopics topics)
        {
            if (FallbackMatcher is null) return false;

            // 必须属于本次请求对应的响应主题，否则可能是其它设备的响应
            if (!topics.IsResponseFor(topic, _context.Topic, _context.DeviceId)) return false;

            // 若响应里明确携带了 @id 且不等于本次请求，则它属于别人 —— 绝不可降级认领。
            // 否则设备回显 @id 时的迟到响应（可能是别的操作甚至别的设备）会被误当成本次结果，
            // 例如文件下载拿到非十六进制内容。
            if (ContainsAnyCorrelationId(payload.Span)) return false;

            var context = _context.For(payload);
            context.CorrelationMatched = false;
            return FallbackMatcher(context);
        }

        /// <summary>
        /// 报文体是否携带了 <c>@id</c>（无论是否等于本次请求）。
        ///
        /// 只回答"有没有"，不解析它的值——调用方从不使用那个值（此前有个
        /// <c>out echoed</c> 参数始终被丢弃）。
        /// </summary>
        private static bool ContainsAnyCorrelationId(ReadOnlySpan<byte> payload)
        {
            var marker = "\"@id\":\""u8;
            var index = payload.IndexOf(marker);
            if (index < 0) return false;

            // 值必须有收尾引号，否则只是报文里的字样而算不上一个 @id 字段
            return payload[(index + marker.Length)..].IndexOf((byte)'"') >= 0;
        }
    }

    #endregion

}

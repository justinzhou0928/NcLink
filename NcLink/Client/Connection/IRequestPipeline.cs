using NcLink.Protocol;
using NcLink.Client.Connection;

namespace NcLink.Client.Connection;

/// <summary>
/// 请求管线：负责"发请求并等到正确的响应"这件事的机械部分。
///
/// 职责边界：连接建立、响应主题订阅、把请求交给 <c>RequestCorrelator</c>、
/// 按超时与取消语义抛异常。<b>不含任何领域含义</b>——
/// 探测、查询、设置各自提供匹配器（<c>matcher</c>）来决定"什么算我的响应"。
/// </summary>
internal interface IRequestPipeline
{
    /// <summary>连接状态。</summary>
    NcLinkConnectionState ConnectionState { get; }

    /// <summary>建立连接（幂等）。</summary>
    Task EnsureConnectedAsync(CancellationToken cancellationToken);

    /// <summary>
    /// 发送请求并等待响应。
    /// </summary>
    /// <param name="topic">请求主题。</param>
    /// <param name="context">匹配上下文（<c>ItemIds</c> 供降级匹配使用）。</param>
    /// <param name="request">请求报文，其 <c>CorrelationId</c> 由关联器填写。</param>
    /// <param name="matcher">语义校验：判断这条响应是否是本次请求要的。</param>
    /// <param name="timeout">超时；为 null 时用默认值。</param>
    Task<RawMessage> SendAsync(
        string topic,
        RequestContext context,
        NcLinkEnvelope request,
        Func<RequestContext, bool> matcher,
        TimeSpan? timeout,
        CancellationToken cancellationToken);
}

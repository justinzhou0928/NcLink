using NcLink.Protocol.Contracts;
using NcLink.Client.Connection.Engine;

namespace NcLink.Client.Connection.Interfaces;

/// <summary>
/// 请求-响应关联器：负责发布请求、等待并匹配响应、超时处理。
///
/// 它是整个客户端里最容易出错的部分，因此独立成组件并配套单元测试。
/// 匹配上下文（<see cref="RequestContext"/>）由关联器回填后交给匹配器。
/// </summary>
internal interface IRequestCorrelator
{
    /// <summary>
    /// 发送请求并等待响应。
    /// </summary>
    /// <param name="request">请求报文（其 <c>CorrelationId</c> 会被填写）。</param>
    /// <param name="context">
    /// 匹配上下文：<see cref="RequestContext.ResponseTopic"/> 等字段由关联器回填，
    /// 匹配器通过 <paramref name="strictMatcher"/> / <paramref name="fallbackMatcher"/> 读取。
    /// </param>
    /// <param name="strictMatcher">
    /// 严格匹配时附加的语义校验（可选）。当 <c>@id</c> 命中且响应主题归属正确时调用；
    /// 未提供则以 <c>@id</c> 为准。可用于进一步确认响应结构。
    /// </param>
    /// <param name="fallbackMatcher">
    /// 降级匹配：仅当没有任何严格匹配命中时才使用，且仅在响应<b>未携带</b> <c>@id</c> 时生效
    /// （携带了不匹配的 <c>@id</c> 说明该响应属于其它请求，绝不可认领）。
    /// 关联器会先校验响应主题归属，再调用它。
    /// </param>
    /// <param name="timeout">超时时间；为 null 时用默认值。</param>
    Task<RawMessage> SendAsync(
        NcLinkEnvelope request,
        RequestContext context,
        Func<RequestContext, bool>? strictMatcher = null,
        Func<RequestContext, bool>? fallbackMatcher = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default);
}

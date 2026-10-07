
namespace NcLink.Client.Connection.Engine;
/// <summary>
/// 一次请求的关联上下文，供匹配器判定"这条响应是不是我要的"。
///
/// 由关联器在收到响应时通过 <see cref="For"/> 派生一份，填入响应内容后再交给匹配器。
/// </summary>
internal sealed class RequestContext
{
    public RequestContext(string topic, string correlationId, string? deviceId, IReadOnlyList<string> itemIds)
    {
        Topic = topic;
        CorrelationId = correlationId;
        DeviceId = deviceId;
        ItemIds = itemIds;
    }

    /// <summary>请求主题。</summary>
    public string Topic { get; }

    /// <summary>本次请求的 <c>@id</c>。</summary>
    public string CorrelationId { get; }

    /// <summary>目标设备 SN。</summary>
    public string? DeviceId { get; }

    /// <summary>本次请求涉及的数据项 id（降级匹配用）。</summary>
    public IReadOnlyList<string> ItemIds { get; }

    /// <summary>响应报文的主题。</summary>
    public string ResponseTopic { get; init; } = string.Empty;

    /// <summary>响应报文的 UTF-8 文本。</summary>
    public string ResponseText { get; init; } = string.Empty;

    /// <summary>响应报文的原始字节。</summary>
    public byte[] ResponsePayload { get; init; } = [];

    /// <summary>
    /// 本次匹配是否命中了 <c>@id</c> 关联。
    /// true 表示响应明确带上了本次请求的关联 id（可信任）；
    /// false 表示走的是降级匹配（设备没有回显 @id）。
    /// </summary>
    public bool CorrelationMatched { get; set; }

    /// <summary>派生一份携带响应内容的新上下文（匹配器收到的就是它）。</summary>
    public RequestContext For(string responseTopic, string responseText, byte[]? responsePayload = null)
        => new(Topic, CorrelationId, DeviceId, ItemIds)
        {
            ResponseTopic = responseTopic,
            ResponseText = responseText,
            ResponsePayload = responsePayload ?? []
        };
}


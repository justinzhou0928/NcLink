namespace NcLink.Client.Connection.Engine;

/// <summary>
/// 一次请求的关联上下文，供匹配器判定"这条响应是不是我要的"。
///
/// 由关联器在收到响应时通过 <see cref="For"/> 派生一份，填入响应文本后再交给匹配器。
/// </summary>
internal sealed class RequestContext
{
    public RequestContext(string topic, string? deviceId, IReadOnlyList<string> itemIds)
    {
        Topic = topic;
        DeviceId = deviceId;
        ItemIds = itemIds;
    }

    /// <summary>请求主题。</summary>
    public string Topic { get; }

    /// <summary>目标设备 SN。</summary>
    public string? DeviceId { get; }

    /// <summary>本次请求涉及的数据项 id（降级匹配用）。</summary>
    public IReadOnlyList<string> ItemIds { get; }

    /// <summary>响应报文的 UTF-8 文本。</summary>
    public string ResponseText { get; init; } = string.Empty;

    /// <summary>
    /// 本次匹配是否命中了 <c>@id</c> 关联。
    /// true 表示响应明确带上了本次请求的关联 id（可信任）；
    /// false 表示走的是降级匹配（设备没有回显 @id）。
    /// </summary>
    public bool CorrelationMatched { get; set; }

    /// <summary>派生一份携带响应文本的新上下文（匹配器收到的就是它）。</summary>
    public RequestContext For(string responseText)
        => new(Topic, DeviceId, ItemIds) { ResponseText = responseText };
}

using System.Text;

namespace NcLink.Client.Connection;

/// <summary>
/// 一次请求的关联上下文，供匹配器判定"这条响应是不是我要的"。
///
/// 由关联器在收到响应时通过 <see cref="For"/> 派生一份，附上响应字节后再交给匹配器。
///
/// <b>响应按 UTF-8 字节参与匹配，不解码成字符串</b>：匹配只做序数子串查找，
/// 而对合法 UTF-8 来说字节级相等与码点级序数比较等价（匹配模式本身都是 ASCII）。
/// 每条消息都 <c>Encoding.UTF8.GetString</c> 会为整条报文分配一份 UTF-16 副本，
/// 而绝大多数消息并不匹配任何在途请求——`ReadOnlySpan&lt;byte&gt;.IndexOf` 零分配。
/// </summary>
internal sealed class RequestContext
{
    public RequestContext(string topic, string? deviceId, IReadOnlyList<string> itemIds)
    {
        Topic = topic;
        DeviceId = deviceId;
        ItemIds = itemIds;

        // 降级匹配要在报文字节里找 "<id>"。模式只取决于请求，因此在这里算一次，
        // 不要每条消息重算。
        ItemIdPatterns = [.. itemIds.Where(static id => id.Length > 0)
                                    .Select(static id => Encoding.UTF8.GetBytes($"\"{id}\""))];
    }

    /// <summary>派生用的私有构造：共享请求侧的字段，只换响应字节。</summary>
    private RequestContext(RequestContext source, ReadOnlyMemory<byte> responsePayload)
    {
        Topic = source.Topic;
        DeviceId = source.DeviceId;
        ItemIds = source.ItemIds;
        ItemIdPatterns = source.ItemIdPatterns;
        ResponsePayload = responsePayload;
    }

    /// <summary>请求主题。</summary>
    public string Topic { get; }

    /// <summary>目标设备 SN。</summary>
    public string? DeviceId { get; }

    /// <summary>本次请求涉及的数据项 id（降级匹配用）。</summary>
    public IReadOnlyList<string> ItemIds { get; }

    /// <summary>数据项 id 的 UTF-8 模式（<c>"id"</c>，含两侧引号），降级匹配用。</summary>
    public IReadOnlyList<byte[]> ItemIdPatterns { get; }

    /// <summary>响应报文的原始字节。</summary>
    public ReadOnlyMemory<byte> ResponsePayload { get; init; }

    /// <summary>
    /// 本次匹配是否命中了 <c>@id</c> 关联。
    /// true 表示响应明确带上了本次请求的关联 id（可信任）；
    /// false 表示走的是降级匹配（设备没有回显 @id）。
    /// </summary>
    public bool CorrelationMatched { get; set; }

    /// <summary>派生一份携带响应字节的新上下文（匹配器收到的就是它）。</summary>
    public RequestContext For(ReadOnlyMemory<byte> responsePayload)
        => new(this, responsePayload);
}

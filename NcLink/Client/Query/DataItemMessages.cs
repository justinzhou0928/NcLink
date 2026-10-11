using NcLink.Client.Devices;
using System.Text.Json;
using NcLink.Client.Connection;
using NcLink.Client.Query;
using NcLink.Protocol;

namespace NcLink.Client.Query;

/// <summary>
/// 数据项报文的构造与匹配工具，供 <see cref="DataItemReader"/>（查询）与
/// <c>DataItemWriter</c>（设置）共用。
///
/// 查询与设置走的是同一个数据项端点（官方手册 5.1），
/// 因此报文的构造规则、响应匹配规则是共享的；区别只在操作名与响应结构。
/// </summary>
internal static class DataItemMessages
{
    /// <summary>查询响应的结构标记：响应里必须出现 <c>values</c>。</summary>
    public static readonly ReadOnlyMemory<byte> QueryStructureMarker = "\"values\""u8.ToArray();

    /// <summary>设置响应的结构标记：响应里必须出现 <c>results</c>。</summary>
    public static readonly ReadOnlyMemory<byte> SetStructureMarker = "\"results\""u8.ToArray();

    /// <summary>解析数据项路径 → 模型 id。</summary>
    public static IReadOnlyList<ResolvedItem> Resolve(DeviceModel model, IEnumerable<DataItemRequest> items)
    {
        var resolved = new List<ResolvedItem>();
        foreach (var item in items)
            resolved.Add(new ResolvedItem(model.GetRequiredId(item.Path), item));

        return resolved;
    }

    /// <summary>
    /// 数据项请求的公共骨架：发请求 → 解码。
    ///
    /// 查询与设置走同一个数据项端点（官方手册 5.1），这几步逐字相同
    /// ——分开各写一份的话，改一处（例如给 <see cref="RequestContext"/> 加个字段）
    /// 必须记得改另一处，漏了就静默分叉。
    /// </summary>
    /// <param name="structureMarker">响应必须具有的结构标记（查询是 <c>"values"</c>，设置是 <c>"results"</c>）。</param>
    /// <param name="label">解析失败时用于异常的可读名（"查询" / "设置"）。</param>
    public static async Task<TResponse> ExchangeAsync<TResponse>(
        RequestPipeline pipeline,
        INcLinkCodec codec,
        string topic,
        string deviceId,
        IReadOnlyList<ResolvedItem> resolved,
        NcLinkEnvelope request,
        ReadOnlyMemory<byte> structureMarker,
        string label,
        TimeSpan? timeout,
        CancellationToken cancellationToken)
        where TResponse : NcLinkEnvelope
    {
        var response = await pipeline.SendAsync(
            topic,
            new RequestContext(topic, deviceId, resolved.Select(r => r.ItemId).ToArray()),
            request,
            matcher: ctx => IsExpectedResponse(ctx, structureMarker.Span),
            timeout: timeout,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return codec.Decode<TResponse>(response.Payload)
               ?? throw new NcLinkProtocolException(
                   $"{label}响应解析失败：{response.Topic}", topic: response.Topic);
    }

    /// <summary>
    /// 响应是否符合本次请求的预期。
    ///
    /// 传入的上下文已通过主题归属校验（由关联器完成），这里再确认：
    /// <list type="number">
    /// <item>响应具有对应结构（取值是 <c>values</c>，设值是 <c>results</c>）——
    /// 排除"同一设备上另一类请求的响应被误领"；</item>
    /// <item>响应内容确属本次请求</item>
    /// </list>
    ///
    /// 第 2 点的判定取决于设备有没有回显 <c>@id</c>（<see cref="RequestContext.CorrelationMatched"/>）：
    /// <list type="bullet">
    /// <item>命中 <c>@id</c>：归属已确定，结构对就算本次响应。
    /// 即使 <c>values</c> 为空也应认领——那是"设备回了但没数据"，
    /// 与"设备没回"要给出不同的错误；</item>
    /// <item>未命中（设备不回显 <c>@id</c>）：只能退而检查响应里是否出现本次请求的数据项 id。</item>
    /// </list>
    ///
    /// <b>已知局限</b>：降级那一路只比 id、不逐项比对 <c>params</c>（厂家
    /// <c>QueryResponseItem.match</c> 会比 <c>offset/length/indexes/keys/operation</c>）。
    /// 若同一设备上<b>并发</b>存在两个涉及同一数据项 id、而 params 不同的请求，
    /// 响应可能被先遍历到的那个认领。
    ///
    /// <b>当前不可达</b>：文件上传与下载都是顺序发块（<c>while</c> 里 <c>await</c>，
    /// <c>offset</c> 递进），任何时刻只有一个分块请求在途。
    /// <b>若将来引入并行分块，必须先在这里补齐 params 比对</b>，否则会写错偏移。
    /// </summary>
    public static bool IsExpectedResponse(RequestContext context, ReadOnlySpan<byte> structureMarker)
    {
        var payload = context.ResponsePayload.Span;

        if (payload.IndexOf(structureMarker) < 0) return false;

        return context.CorrelationMatched || ContainsQuotedIds(payload, context.ItemIdPatterns);
    }

    /// <summary>报文体是否带引号地出现了请求的某个数据项 id。</summary>
    private static bool ContainsQuotedIds(
        ReadOnlySpan<byte> payload, IReadOnlyList<byte[]> itemIdPatterns)
    {
        foreach (var pattern in itemIdPatterns)
            if (payload.IndexOf(pattern) >= 0) return true;

        return false;
    }

    /// <summary>构造查询参数；无参数时返回 null（报文里不出现 params）。</summary>
    public static QueryParamsMessage? BuildQueryParams(DataItemRequest item)
    {
        var hasParams = item.Indexes is { Count: > 0 }
                        || item.Keys is { Count: > 0 }
                        || item.Offset is not null
                        || item.Length is not null
                        || item.Operation is not null;

        if (!hasParams) return null;

        // 厂家 QueryParams.isValid()：indexes 与 keys 不能同时出现，
        // 违反时它的 builder 直接抛 InvalidMessageException（报文根本不发）。
        // 我们此前会一起发出去，设备侧语义未定义。
        if (item.Indexes is { Count: > 0 } && item.Keys is { Count: > 0 })
            throw new NcLinkInvalidRequestException("查询参数里 indexes 与 keys 不能同时出现。");

        return new QueryParamsMessage
        {
            Indexes = item.Indexes?.Select(i => i.ToString()).ToList(),
            Keys = item.Keys?.ToList(),
            Offset = item.Offset,
            Length = item.Length,
            Operation = item.Operation is { } op ? NcLinkOperationNames.ToWire(op) : null
        };
    }

    /// <summary>
    /// 构造设置参数（含位偏移 <c>offset</c>）。
    ///
    /// <b>设置的 params 只能表达单个 <c>index</c> 与单个 <c>key</c></b>
    /// （厂家的 <c>SetParams</c> 就是 <c>Integer index</c> + <c>String key</c>）。
    /// 区间与多值在协议里无法表达，因此这里<b>显式拒绝</b>而不是只发第一个——
    /// 后者会让调用方以为整段都写了，是静默的数据错误。
    /// </summary>
    public static SetParamsMessage BuildSetParams(DataItemRequest item)
    {
        var index = ResolveSingleIndex(item.Indexes);
        var key = ResolveSingleKey(item.Keys);

        // 厂家 SetParams.isValid()：index 与 key 不能同时出现
        if (index is not null && key is not null)
            throw new NcLinkInvalidRequestException("设置参数里 index 与 key 不能同时出现。");

        return new SetParamsMessage
        {
            Index = index,
            Offset = item.BitOffset,
            Length = item.Length,
            Key = key,
            Operation = item.Operation is { } op ? NcLinkOperationNames.ToWire(op) : null,
            Value = item.Value is null ? null : ToJsonElement(item.Value)
        };
    }

    /// <summary>取唯一的单点索引；区间或多值直接拒绝（协议无法表达）。</summary>
    private static long? ResolveSingleIndex(IReadOnlyList<IndexRange>? indexes)
    {
        if (indexes is not { Count: > 0 }) return null;

        if (indexes is not [{ IsSingle: true } single])
            throw new NcLinkInvalidRequestException(
                $"设置只支持单个单点索引，收到 {indexes.Count} 项。"
                + "要写一段请用 IndexRange.Enumerate() 展开后逐项调用。");

        return single.Start;
    }

    /// <summary>取唯一的字符串 key；多值直接拒绝（协议无法表达）。</summary>
    private static string? ResolveSingleKey(IReadOnlyList<string>? keys)
    {
        if (keys is not { Count: > 0 }) return null;

        if (keys is not [var single])
            throw new NcLinkInvalidRequestException($"设置只支持单个 key，收到 {keys.Count} 项。");

        return single;
    }

    /// <summary>把任意 CLR 值转成 <see cref="JsonElement"/>。</summary>
    private static JsonElement ToJsonElement(object value)
        => JsonSerializer.SerializeToElement(value, value.GetType(), NcLinkJson.Options);
}

/// <summary>已解析的数据项：模型 id + 原始请求。</summary>
internal readonly record struct ResolvedItem(string ItemId, DataItemRequest Item);

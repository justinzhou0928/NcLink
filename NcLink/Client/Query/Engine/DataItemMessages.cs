using System.Text.Json;
using NcLink.Client.Connection.Engine;
using NcLink.Client.Query.Models;
using NcLink.Protocol;
using NcLink.Protocol.Contracts;
using NcLink.Client.Devices.Models;

namespace NcLink.Client.Query.Engine;

/// <summary>
/// 数据项报文的构造与匹配工具，供 <see cref="DataItemReader"/>（查询）与
/// <c>DataItemWriter</c>（设置）共用。
///
/// 查询与设置走的是同一个数据项端点（官方手册 5.1），
/// 因此报文的构造规则、响应匹配规则是共享的；区别只在操作名与响应结构。
/// </summary>
internal static class DataItemMessages
{
    /// <summary>解析数据项路径 → 模型 id。</summary>
    public static IReadOnlyList<ResolvedItem> Resolve(DeviceModel model, IEnumerable<DataItemRequest> items)
    {
        var resolved = new List<ResolvedItem>();
        foreach (var item in items)
            resolved.Add(new ResolvedItem(model.GetRequiredId(item.Path), item));

        return resolved;
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
    /// </summary>
    public static bool IsExpectedResponse(RequestContext context, string structureMarker)
    {
        if (!context.ResponseText.Contains(structureMarker, StringComparison.Ordinal)) return false;

        return context.CorrelationMatched
               || ContainsQuotedIds(context.ResponseText, context.ItemIds);
    }

    /// <summary>报文体是否带引号地出现了请求的某个数据项 id。</summary>
    private static bool ContainsQuotedIds(string text, IReadOnlyList<string> itemIds)
    {
        foreach (var id in itemIds)
        {
            if (id.Length == 0) continue;
            if (text.Contains("\"" + id + "\"", StringComparison.Ordinal)) return true;
        }
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

        return new QueryParamsMessage
        {
            Indexes = item.Indexes?.Select(i => i.ToString()).ToList(),
            Keys = item.Keys?.ToList(),
            Offset = item.Offset,
            Length = item.Length,
            Operation = item.Operation is { } op ? NcLinkOperationNames.ToWire(op) : null
        };
    }

    /// <summary>构造设置参数（含位偏移 <c>offset</c>）。</summary>
    public static SetParamsMessage BuildSetParams(DataItemRequest item)
    {
        // 位操作走 offset 字段；只有单个索引时才发 index
        var singleIndex = item.Indexes is { Count: 1 } ? item.Indexes[0].Start : (long?)null;

        return new SetParamsMessage
        {
            Index = singleIndex,
            Offset = item.BitOffset,
            Key = item.Keys is { Count: 1 } ? item.Keys[0] : null,
            Operation = item.Operation is { } op ? NcLinkOperationNames.ToWire(op) : null,
            Value = item.Value is null ? null : ToJsonElement(item.Value)
        };
    }

    /// <summary>把任意 CLR 值转成 <see cref="JsonElement"/>。</summary>
    private static JsonElement ToJsonElement(object value)
        => JsonSerializer.SerializeToElement(value, value.GetType(), NcLinkJson.Options);
}

/// <summary>已解析的数据项：模型 id + 原始请求。</summary>
internal readonly record struct ResolvedItem(string ItemId, DataItemRequest Item);

using System.Text.Json;
using NcLink.Client.Devices;
using NcLink.Client.Query;
using NcLink.ApiServer.Models;
using NcLink.Protocol;

namespace NcLink.ApiServer.Operations;

/// <summary>
/// 把 HTTP 层的 <see cref="DataRequestItem"/> 映射成库的 <see cref="DataItemRequest"/>。
///
/// 映射规则全部来自对官方 apiServer 的<b>实测</b>
/// （见 <c>docs/api-server-conformance.md</c>）：
/// <list type="bullet">
/// <item><c>index: 0</c> → 1 个值；<c>index: [0,1,2,3]</c> → 4 个值；
///   <c>index: "0-7"</c> → 官方 <c>code=4</c> 拒绝（我们同样拒绝，不猜语义）；</item>
/// <item><c>offset</c> / <c>length</c> 对数据项<b>不生效</b>（官方实测返回 <c>[null]</c>），
///   因此这里不把它们传给数据项请求；<c>offset</c> 仅在 <c>set_value</c> 时表示位偏移。</item>
/// </list>
/// </summary>
internal static class DataRequestMapper
{
    /// <summary>把一项 HTTP 数据项映射为库请求。参数非法时返回 null 并给出原因。</summary>
    public static DataItemRequest? ToDataItemRequest(
        DataRequestItem item, string? operation, out string? error, out int errorCode)
    {
        error = null;
        // 实测：path 问题 → 3；operation 非法 → 2；key 空串 → 19；参数形状 → 4
        errorCode = DataResponse.CodeRequestBadIndex;

        if (string.IsNullOrWhiteSpace(item.Path))
        {
            error = "items[].path 不能为空。";
            errorCode = DataResponse.CodeRequestBadPath;
            return null;
        }

        // 实测：operation <b>缺失</b>默认 get_value；显式给空串/全空格是非法（code 2）。
        // 因此这里只在 null 时跳过校验，空串必须走到 TryParse 去失败。
        NcLinkOperation? op = null;
        if (operation is not null)
        {
            if (!NcLinkOperationNames.TryParse(operation, out var parsed))
            {
                error = $"不支持的 operation：{operation}。合法值为 "
                      + "get_value / set_value / get_length / get_keys / get_attributes / add / delete。";
                errorCode = DataResponse.CodeRequestBadOperation;
                return null;
            }
            op = parsed;
        }

        var indexes = ParseIndexes(item.Index, out error);
        if (error is not null) return null;

        var keys = ParseKeys(item.Key, out var keyError, out var keyEmpty);
        if (keyError is not null)
        {
            error = keyError;
            // 实测：key 传空串 → code 19（厂家 EXECUTE_ERR_OTHER），2 ms 内立即返回
            errorCode = keyEmpty ? DataResponse.CodeExecuteOther : DataResponse.CodeRequestBadIndex;
            return null;
        }

        return new DataItemRequest
        {
            Path = item.Path!,
            Indexes = indexes,
            Keys = keys,
            Operation = op,
            // offset 只在 set_value 时表示位偏移
            BitOffset = op == NcLinkOperation.SetValue ? item.Offset : null,
            Value = ToClrValue(item.Value)
        };
    }

    /// <summary>
    /// 解析官方 <c>index</c>（<c>Object</c>）：单个整数或整数数组。
    /// 区间字符串被官方拒绝，这里也拒绝并给出同样的语义。
    /// </summary>
    private static IReadOnlyList<IndexRange>? ParseIndexes(JsonElement? index, out string? error)
    {
        error = null;

        if (index is not { } value || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;

        switch (value.ValueKind)
        {
            case JsonValueKind.Number:
                if (!value.TryGetInt32(out var single))
                {
                    error = "items[].index 不是合法整数。";
                    return null;
                }
                return [IndexRange.Single(single)];

            case JsonValueKind.Array:
                var ranges = new List<IndexRange>();
                foreach (var element in value.EnumerateArray())
                {
                    if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out var i))
                    {
                        error = "items[].index 数组里含有非整数元素。";
                        return null;
                    }
                    ranges.Add(IndexRange.Single(i));
                }
                return ranges.Count > 0 ? ranges : null;

            // 官方对区间字符串返回 code=4，这里同样拒绝（不猜语义）
            case JsonValueKind.String:
                error = $"items[].index 不接受字符串形式（{value.GetString()}）；"
                      + "请传整数或整数数组。";
                return null;

            default:
                error = $"items[].index 类型不支持：{value.ValueKind}。";
                return null;
        }
    }

    /// <summary>
    /// 解析 <c>key</c>：单个字符串或字符串数组。
    ///
    /// 实测：<c>key</c> 传<b>空串</b>时官方返回 <c>code 19</c>（目标对象不存在），
    /// 而不是当成"没给 key"——<c>key</c> 缺失才是后者。
    /// 通过 <paramref name="empty"/> 把这个区别告诉调用方。
    /// </summary>
    private static IReadOnlyList<string>? ParseKeys(JsonElement? key, out string? error, out bool empty)
    {
        error = null;
        empty = false;
        if (key is not { } value || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;

        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                if (string.IsNullOrEmpty(value.GetString()))
                {
                    error = "items[].key 是空字符串。";
                    empty = true;
                    return null;
                }
                return [value.GetString()!];

            case JsonValueKind.Array:
                var keys = new List<string>();
                foreach (var element in value.EnumerateArray())
                {
                    if (element.ValueKind != JsonValueKind.String)
                    {
                        error = "items[].key 数组里含有非字符串元素。";
                        return null;
                    }
                    keys.Add(element.GetString()!);
                }
                return keys.Count > 0 ? keys : null;

            default:
                error = $"items[].key 类型不支持：{value.ValueKind}。";
                return null;
        }
    }

    /// <summary>
    /// <c>value</c> 转 CLR 值交给库序列化。
    ///
    /// 用 <see cref="JsonElement.Clone"/> 保证脱离原文档仍可用；
    /// 注意 <c>JsonElement</c> 的 <c>Undefined</c> 不等于 JSON null，
    /// 因此显式区分（见 AGENTS.md 的同名红线）。
    /// </summary>
    private static object? ToClrValue(JsonElement? value)
    {
        if (value is not { } element || element.ValueKind == JsonValueKind.Undefined)
            return null;

        return element.ValueKind == JsonValueKind.Null ? null : element.Clone();
    }
}

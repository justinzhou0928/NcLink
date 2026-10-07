using System.Text.Json;

namespace NcLink.Protocol.Contracts;

/// <summary>
/// <see cref="JsonElement"/> 取值辅助。
/// 设备返回值类型不固定（整数／浮点／字符串／对象／null），这里集中做安全转换。
/// </summary>
public static class NcJsonValue
{
    /// <summary>转 int；浮点截断，数字字符串可解析。</summary>
    public static int ToInt32(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Number:
                return element.TryGetInt32(out var i) ? i : (int)element.GetDouble();
            case JsonValueKind.String:
                var s = element.GetString();
                if (int.TryParse(s, out var parsedInt)) return parsedInt;
                if (double.TryParse(s, out var parsedDouble)) return (int)parsedDouble;
                throw new FormatException($"无法转换为整数：{s}");
            case JsonValueKind.True: return 1;
            case JsonValueKind.False: return 0;
            case JsonValueKind.Null:
            case JsonValueKind.Undefined: return 0;
            default:
                throw new FormatException($"无法转换为整数：{element.ValueKind}");
        }
    }

    /// <summary>转 long。</summary>
    public static long ToInt64(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Number:
                return element.TryGetInt64(out var l) ? l : (long)element.GetDouble();
            case JsonValueKind.String:
                var s = element.GetString();
                if (long.TryParse(s, out var parsedLong)) return parsedLong;
                if (double.TryParse(s, out var parsedDouble)) return (long)parsedDouble;
                throw new FormatException($"无法转换为长整数：{s}");
            case JsonValueKind.True: return 1;
            case JsonValueKind.False: return 0;
            case JsonValueKind.Null:
            case JsonValueKind.Undefined: return 0;
            default:
                throw new FormatException($"无法转换为长整数：{element.ValueKind}");
        }
    }

    /// <summary>转 double。</summary>
    public static double ToDouble(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Number: return element.GetDouble();
            case JsonValueKind.String:
                var s = element.GetString();
                if (double.TryParse(s, out var parsed)) return parsed;
                throw new FormatException($"无法转换为浮点数：{s}");
            case JsonValueKind.True: return 1;
            case JsonValueKind.False: return 0;
            case JsonValueKind.Null:
            case JsonValueKind.Undefined: return 0;
            default:
                throw new FormatException($"无法转换为浮点数：{element.ValueKind}");
        }
    }

    /// <summary>转字符串；null／undefined 返回 null（用于表达"该项无值"）。</summary>
    public static string? ToStringOrNull(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.String => element.GetString(),
        _ => element.GetRawText()
    };

    /// <summary>读取对象属性；不存在时返回 null。</summary>
    public static JsonElement? TryGetProperty(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            ? value
            : null;
}

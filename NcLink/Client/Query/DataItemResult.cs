using System.Text.Json;
using NcLink.Protocol;

namespace NcLink.Client.Query;

/// <summary>
/// 单个数据项的查询结果。
/// 设备返回的值类型不固定（整数、浮点、字符串、对象），故保留 <see cref="JsonElement"/>，
/// 并提供常用转换方法。
/// </summary>
public sealed class DataItemResult
{
    public DataItemResult(string itemId, bool succeeded, string? reason, IReadOnlyList<JsonElement> values)
    {
        ItemId = itemId;
        Succeeded = succeeded;
        Reason = reason;
        Values = values;
    }

    /// <summary>数据项在模型中的 id。</summary>
    public string ItemId { get; }

    /// <summary>设备是否返回 <c>OK</c>。</summary>
    public bool Succeeded { get; }

    /// <summary>设备给出的失败原因（若有）。</summary>
    public string? Reason { get; }

    /// <summary>值序列。</summary>
    public IReadOnlyList<JsonElement> Values { get; }

    /// <summary>便捷状态码。</summary>
    public NcLinkCode Code => Succeeded ? NcLinkCode.Ok : NcLinkCode.Ng;

    public int Count => Values.Count;

    public int IntAt(int index) => NcJsonValue.ToInt32(Values[index]);

    public long LongAt(int index) => NcJsonValue.ToInt64(Values[index]);

    public double DoubleAt(int index) => NcJsonValue.ToDouble(Values[index]);

    /// <summary>取字符串；值缺失（null）时返回 null。</summary>
    public string? StringAt(int index) => NcJsonValue.ToStringOrNull(Values[index]);

    public int[] ToIntArray() => Values.Select(NcJsonValue.ToInt32).ToArray();

    public long[] ToLongArray() => Values.Select(NcJsonValue.ToInt64).ToArray();

    public double[] ToDoubleArray() => Values.Select(NcJsonValue.ToDouble).ToArray();

    public string?[] ToStringArray() => Values.Select(NcJsonValue.ToStringOrNull).ToArray();
}

/// <summary>单个数据项的设置结果。</summary>
public sealed class SetItemResult
{
    public SetItemResult(string itemId, bool succeeded, string? reason = null)
    {
        ItemId = itemId;
        Succeeded = succeeded;
        Reason = reason;
    }

    public string ItemId { get; }

    public bool Succeeded { get; }

    public string? Reason { get; }
}

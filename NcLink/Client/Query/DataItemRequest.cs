using NcLink.Client.Devices;
using NcLink.Protocol;

namespace NcLink.Client.Query;

/// <summary>
/// 一个数据项的操作描述。同时用于查询与设置。
/// </summary>
public sealed class DataItemRequest
{
    /// <summary>数据项路径、标识或模型 id。</summary>
    public required string Path { get; init; }

    /// <summary>索引区间；为空表示整项。</summary>
    public IReadOnlyList<IndexRange>? Indexes { get; init; }

    /// <summary>HASH 类型数据项的键。</summary>
    public IReadOnlyList<string>? Keys { get; init; }

    /// <summary>二进制位偏移（设置寄存器某一位）。</summary>
    public int? BitOffset { get; init; }

    /// <summary>长度（文件分块、字符串取长）。</summary>
    public long? Length { get; init; }

    /// <summary>字节偏移（文件分块）。</summary>
    public long? Offset { get; init; }

    /// <summary>操作类型；为空表示按调用场景默认（查询 <c>get_value</c>，设置 <c>set_value</c>）。</summary>
    public NcLinkOperation? Operation { get; init; }

    /// <summary>设置的目标值。</summary>
    public object? Value { get; init; }

    /// <summary>按路径查询整项。</summary>
    public static DataItemRequest At(string path) => new() { Path = path };

    /// <summary>按路径查询若干索引。</summary>
    public static DataItemRequest At(string path, params int[] indexes)
        => new() { Path = path, Indexes = indexes.Select(IndexRange.Single).ToArray() };

    /// <summary>按路径查询一个区间。</summary>
    public static DataItemRequest At(string path, int from, int to)
        => new() { Path = path, Indexes = [IndexRange.FromTo(from, to)] };

    /// <summary>设置某个索引的值。</summary>
    public static DataItemRequest Set(string path, object value, int? index = null, int? bitOffset = null)
        => new()
        {
            Path = path,
            Value = value,
            Indexes = index is null ? null : [IndexRange.Single(index.Value)],
            BitOffset = bitOffset
        };
}

namespace NcLink.Client.Devices;

/// <summary>
/// NC-Link 索引区间。协议里索引用字符串表达：单点 <c>"13"</c>、区间 <c>"0-9"</c>。
/// </summary>
public readonly record struct IndexRange
{
    public int Start { get; }

    /// <summary>区间结束下标；为 null 表示单点。</summary>
    public int? End { get; }

    public IndexRange(int index)
    {
        if (index < 0) throw new ArgumentOutOfRangeException(nameof(index), index, "索引不能为负。");
        Start = index;
        End = null;
    }

    public IndexRange(int start, int end)
    {
        if (start < 0) throw new ArgumentOutOfRangeException(nameof(start), start, "索引不能为负。");
        if (end < start) throw new ArgumentException($"区间非法：{start}-{end}（结束下标小于起始下标）。", nameof(end));
        Start = start;
        End = end;
    }

    public bool IsSingle => End is null;

    public int Count => IsSingle ? 1 : End!.Value - Start + 1;

    /// <summary>序列化为协议字符串："13" 或 "0-9"。</summary>
    public override string ToString() => IsSingle ? Start.ToString() : $"{Start}-{End}";

    /// <summary>解析协议字符串；支持 "13"、"0-9"。</summary>
    public static IndexRange Parse(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        var trimmed = text.Trim();
        var dash = trimmed.IndexOf('-', 1);   // 从 1 开始，允许负数不受影响的分析
        if (dash < 0)
            return new IndexRange(int.Parse(trimmed));

        var start = int.Parse(trimmed[..dash]);
        var end = int.Parse(trimmed[(dash + 1)..]);
        return new IndexRange(start, end);
    }

    public static bool TryParse(string? text, out IndexRange range)
    {
        range = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        try { range = Parse(text); return true; }
        catch (Exception e) when (e is FormatException or ArgumentException or OverflowException) { return false; }
    }

    public static IndexRange Single(int index) => new(index);

    public static IndexRange FromTo(int start, int end) => new(start, end);

    /// <summary>枚举区间内全部下标。</summary>
    public IEnumerable<int> Enumerate()
    {
        if (IsSingle) { yield return Start; yield break; }
        for (var i = Start; i <= End!.Value; i++) yield return i;
    }
}

namespace NcLink.Protocol.Contracts;

/// <summary>
/// 数据项操作类型。序列化值与官方 <c>cn.nerc.nclink.core.general.Operation</c> 一致。
/// </summary>
public enum NcLinkOperation
{
    /// <summary>取值。</summary>
    GetValue,

    /// <summary>取长度。</summary>
    GetLength,

    /// <summary>取键（HASH 类型，如 G 代码文件列表）。</summary>
    GetKeys,

    /// <summary>取属性（如文件大小、修改时间）。</summary>
    GetAttributes,

    /// <summary>设值。</summary>
    SetValue,

    /// <summary>添加（如新建文件夹）。</summary>
    Add,

    /// <summary>删除（如删除文件／文件夹）。</summary>
    Delete
}

/// <summary>协议线格式的操作名。</summary>
public static class NcLinkOperationNames
{
    public const string GetValue = "get_value";
    public const string GetLength = "get_length";
    public const string GetKeys = "get_keys";
    public const string GetAttributes = "get_attributes";
    public const string SetValue = "set_value";
    public const string Add = "add";
    public const string Delete = "delete";

    public static string ToWire(NcLinkOperation operation) => operation switch
    {
        NcLinkOperation.GetValue => GetValue,
        NcLinkOperation.GetLength => GetLength,
        NcLinkOperation.GetKeys => GetKeys,
        NcLinkOperation.GetAttributes => GetAttributes,
        NcLinkOperation.SetValue => SetValue,
        NcLinkOperation.Add => Add,
        NcLinkOperation.Delete => Delete,
        _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, "未知操作类型。")
    };

    public static bool TryParse(string? wire, out NcLinkOperation operation)
    {
        switch (wire)
        {
            case GetValue: operation = NcLinkOperation.GetValue; return true;
            case GetLength: operation = NcLinkOperation.GetLength; return true;
            case GetKeys: operation = NcLinkOperation.GetKeys; return true;
            case GetAttributes: operation = NcLinkOperation.GetAttributes; return true;
            case SetValue: operation = NcLinkOperation.SetValue; return true;
            case Add: operation = NcLinkOperation.Add; return true;
            case Delete: operation = NcLinkOperation.Delete; return true;
            default: operation = default; return false;
        }
    }
}

/// <summary>设备侧单项返回码。</summary>
public enum NcLinkCode
{
    Unknown = 0,

    /// <summary>成功。</summary>
    Ok,

    /// <summary>失败。</summary>
    Ng
}

/// <summary>返回码解析。</summary>
public static class NcLinkCodeNames
{
    public const string Ok = "OK";
    public const string Ng = "NG";

    public static NcLinkCode Parse(string? wire) => wire?.ToUpperInvariant() switch
    {
        Ok => NcLinkCode.Ok,
        Ng => NcLinkCode.Ng,
        _ => NcLinkCode.Unknown
    };

    public static string ToWire(NcLinkCode code) => code switch
    {
        NcLinkCode.Ok => Ok,
        NcLinkCode.Ng => Ng,
        _ => string.Empty
    };
}

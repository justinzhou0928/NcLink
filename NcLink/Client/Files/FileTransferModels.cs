
namespace NcLink.Client.Files;
/// <summary>G 代码文件信息。</summary>
public sealed class FileEntry
{
    public FileEntry(string name, long size, string? type = null, string? changeTime = null)
    {
        Name = name;
        Size = size;
        Type = type;
        ChangeTime = changeTime;
    }

    /// <summary>文件名（可能含相对目录，如 <c>AAA/OTEST</c>）。</summary>
    public string Name { get; }

    /// <summary>字节数。</summary>
    public long Size { get; }

    /// <summary><c>file</c> 或 <c>directory</c>。</summary>
    public string? Type { get; }

    /// <summary>设备给出的修改时间字符串。</summary>
    public string? ChangeTime { get; }
}

/// <summary>文件传输进度。</summary>
public sealed class FileTransferProgress
{
    public FileTransferProgress(long transferred, long? total)
    {
        Transferred = transferred;
        Total = total;
    }

    /// <summary>已传输字节数。</summary>
    public long Transferred { get; }

    /// <summary>总字节数；未知时为 null。</summary>
    public long? Total { get; }

    /// <summary>进度比例（0-1）；总量未知时为 null。</summary>
    public double? Fraction => Total is > 0 ? (double)Transferred / Total.Value : null;
}

/// <summary>
/// 文件传输的协议限制（常量，便于作为默认参数值使用）。
/// </summary>
public static class FileTransferLimits
{
    /// <summary>协议允许的单次传输上限（100 KiB）。</summary>
    public const int MaxBlockSize = 100 * 1024;

    /// <summary>建议的分块大小（50 KiB）。</summary>
    public const int RecommendedBlockSize = 50 * 1024;
}

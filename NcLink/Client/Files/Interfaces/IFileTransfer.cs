using NcLink.Client.Files.Models;

namespace NcLink.Client.Files.Interfaces;
/// <summary>
/// G 代码文件上下行。
///
/// 协议要点（《HNC8-NOM 接口参考手册》第 8 章）：
/// 文件内容用<b>十六进制字符串</b>表达；数控系统 G 代码默认编码为 <b>GB2312</b>；
/// 单次传输不超过 100K，大文件建议按 50K 分块，用 offset + length 控制。
///
/// 配套的数据形状（<c>FileEntry</c> / <c>FileTransferProgress</c> /
/// <c>FileTransferLimits</c>）在 <see cref="NcLink.Client.Files.Models"/> 下。
/// </summary>
public interface IFileTransfer
{
    /// <summary>列出 G 代码目录下的文件名（设备会遍历整个 G 目录）。</summary>
    Task<IReadOnlyList<string>> ListFilesAsync(string deviceId, CancellationToken cancellationToken = default);

    /// <summary>取文件属性。</summary>
    Task<IReadOnlyList<Models.FileEntry>> GetFileEntriesAsync(
        string deviceId,
        IEnumerable<string> fileNames,
        CancellationToken cancellationToken = default);

    /// <summary>下载文件（自动分块）。</summary>
    Task<byte[]> DownloadAsync(
        string deviceId,
        string fileName,
        long? knownSize = null,
        int blockSize = Models.FileTransferLimits.RecommendedBlockSize,
        IProgress<Models.FileTransferProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>下载为文本（默认按 GB2312 解码）。</summary>
    Task<string> DownloadTextAsync(
        string deviceId,
        string fileName,
        long? knownSize = null,
        CancellationToken cancellationToken = default);

    /// <summary>下载并写入本地文件。</summary>
    Task DownloadToFileAsync(
        string deviceId,
        string fileName,
        string localPath,
        long? knownSize = null,
        IProgress<Models.FileTransferProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>下发文件（自动分块）。</summary>
    Task UploadAsync(
        string deviceId,
        string targetPath,
        byte[] content,
        int blockSize = Models.FileTransferLimits.RecommendedBlockSize,
        IProgress<Models.FileTransferProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>下发文本（默认按 GB2312 编码）。</summary>
    Task UploadTextAsync(
        string deviceId,
        string targetPath,
        string text,
        CancellationToken cancellationToken = default);

    /// <summary>在 G 代码目录下新建文件夹。</summary>
    Task<bool> CreateDirectoryAsync(string deviceId, string directoryName, CancellationToken cancellationToken = default);

    /// <summary>删除文件或文件夹（删除文件夹前须先清空内容）。</summary>
    Task<bool> DeleteAsync(string deviceId, string path, CancellationToken cancellationToken = default);
}

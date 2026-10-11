using NcLink.Protocol.Models;
using NcLink.Protocol.Engine;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NcLink.Client.Files.Interfaces;
using NcLink.Client.Files.Models;
using NcLink.Client.Query.Interfaces;
using NcLink.Client.Query.Models;
using NcLink.Client.Set.Interfaces;
using NcLink.Exceptions;

namespace NcLink.Client.Files.Engine;

/// <summary>
/// G 代码文件上下行的默认实现。
///
/// 协议要点（《HNC8-NOM 接口参考手册》第 8 章）：
/// 内容用十六进制字符串表达；G 代码默认编码 GB2312；单次不超过 100K，建议 50K 分块；
/// 下载用 offset + length 分块，下发时 value 的十六进制串长度必须是 length 的两倍。
/// </summary>
internal sealed class FileTransfer : IFileTransfer
{
    private readonly IDataItemReader _reader;
    private readonly IDataItemWriter _writer;
    private readonly ILogger<FileTransfer> _logger;

    /// <summary>
    /// 文件读写都走数据项端点：读内容用查询（<c>get_value</c>），
    /// 写内容与建目录/删除用设置（<c>set_value</c> / <c>add</c> / <c>delete</c>），
    /// 因此两者都要。
    /// </summary>
    public FileTransfer(
        IDataItemReader reader,
        IDataItemWriter writer,
        ILogger<FileTransfer> logger)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    #region 目录

    public async Task<IReadOnlyList<string>> ListFilesAsync(
        string deviceId, CancellationToken cancellationToken = default)
    {
        var result = await QueryAsync(deviceId, NcLinkOperation.GetKeys, cancellationToken).ConfigureAwait(false);
        return result.Values.Select(v => NcJsonValue.ToStringOrNull(v) ?? string.Empty).ToList();
    }

    public async Task<IReadOnlyList<FileEntry>> GetFileEntriesAsync(
        string deviceId, IEnumerable<string> fileNames, CancellationToken cancellationToken = default)
    {
        var names = fileNames.ToList();
        if (names.Count == 0) return [];

        var result = await QueryAsync(deviceId, NcLinkOperation.GetAttributes, cancellationToken,
            keys: names).ConfigureAwait(false);

        var entries = new List<FileEntry>();
        for (var i = 0; i < result.Values.Count; i++)
        {
            var element = result.Values[i];
            var name = i < names.Count ? names[i] : i.ToString();

            // 设备没给出属性对象（例如文件不存在时返回 null）说明<b>取不到信息</b>，
            // 而不是"文件大小是 0"——0 字节文件会返回对象且 size=0。
            // 曾在这里伪造成 size=0 的条目，导致 ResolveSizeAsync 拿到 0、
            // 下载出空内容、上层误报成功（官方同一场景返回"取不到文件大小"错误）。
            if (element.ValueKind != System.Text.Json.JsonValueKind.Object)
                continue;

            var size = NcJsonValue.TryGetProperty(element, "size") is { } sizeElement
                ? NcJsonValue.ToInt64(sizeElement)
                : 0;
            var type = NcJsonValue.TryGetProperty(element, "type") is { } typeElement
                ? NcJsonValue.ToStringOrNull(typeElement)
                : null;
            var changeTime = NcJsonValue.TryGetProperty(element, "changeTime") is { } timeElement
                ? NcJsonValue.ToStringOrNull(timeElement)
                : null;

            entries.Add(new FileEntry(name, size, type, changeTime));
        }

        return entries;
    }

    #endregion

    #region 下载

    public async Task<byte[]> DownloadAsync(
        string deviceId,
        string fileName,
        long? knownSize = null,
        int blockSize = FileTransferLimits.RecommendedBlockSize,
        IProgress<FileTransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ValidateBlockSize(blockSize);

        var total = knownSize ?? await ResolveSizeAsync(deviceId, fileName, cancellationToken).ConfigureAwait(false);
        if (total <= 0) return [];

        // 总长已知（协议要求先 get_attributes 取 size），因此直接按总长预分配并原地填充。
        // 不用 MemoryStream + ToArray()：那会先把数据写进扩容缓冲、再整体复制一份，
        // 峰值内存翻倍。实测 50 MB 下载由 100 MB 分配降到 50 MB。
        var buffer = GC.AllocateUninitializedArray<byte>((int)total);
        var written = 0;
        long offset = 0;

        while (offset < total)
        {
            var length = (int)Math.Min(blockSize, total - offset);

            var result = await QueryAsync(deviceId, NcLinkOperation.GetValue, cancellationToken,
                keys: [fileName], offset: offset, length: length).ConfigureAwait(false);

            if (!result.Succeeded)
                throw new NcLinkDeviceRejectedException(deviceId, result.ItemId, result.Reason);

            if (result.Values.Count == 0) break;

            var hex = NcJsonValue.ToStringOrNull(result.Values[0]);
            if (string.IsNullOrEmpty(hex)) break;

            var chunk = HexToBytes(hex);

            // 设备若返回超出请求长度的数据，截断以避免越界（协议规定不应发生）
            if (chunk.Length > length) chunk = chunk[..length];
            if (chunk.Length == 0) break;

            chunk.CopyTo(buffer, written);
            written += chunk.Length;
            offset += chunk.Length;
            progress?.Report(new FileTransferProgress(offset, total));

            // 设备在文件尾部会返回不足请求长度的数据
            if (chunk.Length < length) break;
        }

        // 提前结束（设备给的数据不足声明长度）时才需要缩一次
        return written == buffer.Length ? buffer : buffer[..written];
    }

    public async Task<string> DownloadTextAsync(
        string deviceId,
        string fileName,
        long? knownSize = null,
        CancellationToken cancellationToken = default)
    {
        var bytes = await DownloadAsync(deviceId, fileName, knownSize, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return GCodeEncoding.Decode(bytes);
    }

    public async Task DownloadToFileAsync(
        string deviceId,
        string fileName,
        string localPath,
        long? knownSize = null,
        IProgress<FileTransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localPath);

        var bytes = await DownloadAsync(deviceId, fileName, knownSize, cancellationToken: cancellationToken,
            progress: progress).ConfigureAwait(false);

        await File.WriteAllBytesAsync(localPath, bytes, cancellationToken).ConfigureAwait(false);
    }

    #endregion

    #region 下发

    public async Task UploadAsync(
        string deviceId,
        string targetPath,
        byte[] content,
        int blockSize = FileTransferLimits.RecommendedBlockSize,
        IProgress<FileTransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        ValidateBlockSize(blockSize);

        long offset = 0;
        while (offset < content.Length)
        {
            var length = (int)Math.Min(blockSize, content.Length - offset);

            // 直接传切片给 BytesToHex（它收 ReadOnlySpan<byte>），
            // 不必先 ToArray() 复制一份 50 KiB 的分块。
            await SetAsync(deviceId, NcLinkOperation.SetValue, cancellationToken,
                key: targetPath, offset: offset, length: length,
                value: BytesToHex(content.AsSpan((int)offset, length))).ConfigureAwait(false);

            offset += length;
            progress?.Report(new FileTransferProgress(offset, content.Length));
        }
    }

    public Task UploadTextAsync(
        string deviceId,
        string targetPath,
        string text,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        return UploadAsync(deviceId, targetPath, GCodeEncoding.Encode(text),
            cancellationToken: cancellationToken);
    }

    public async Task<bool> CreateDirectoryAsync(
        string deviceId, string directoryName, CancellationToken cancellationToken = default)
        => await SetAsync(deviceId, NcLinkOperation.Add, cancellationToken, key: directoryName)
            .ConfigureAwait(false);

    public async Task<bool> DeleteAsync(
        string deviceId, string path, CancellationToken cancellationToken = default)
        => await SetAsync(deviceId, NcLinkOperation.Delete, cancellationToken, key: path).ConfigureAwait(false);

    #endregion

    #region 内部

    private async Task<long> ResolveSizeAsync(string deviceId, string fileName, CancellationToken cancellationToken)
    {
        var entries = await GetFileEntriesAsync(deviceId, [fileName], cancellationToken).ConfigureAwait(false);
        if (entries.Count == 0)
            throw new NcLinkProtocolException($"取不到文件信息：{deviceId} / {fileName}")
            { DeviceId = deviceId };

        return entries[0].Size;
    }

    private Task<DataItemResult> QueryAsync(
        string deviceId,
        NcLinkOperation operation,
        CancellationToken cancellationToken,
        IReadOnlyList<string>? keys = null,
        long? offset = null,
        long? length = null)
    {
        var item = new DataItemRequest
        {
            Path = NcLinkProtocolPaths.File,
            Operation = operation,
            Keys = keys,
            Offset = offset,
            Length = length
        };

        return _reader.QueryItemAsync(deviceId, item, cancellationToken: cancellationToken);
    }

    private async Task<bool> SetAsync(
        string deviceId,
        NcLinkOperation operation,
        CancellationToken cancellationToken,
        string? key = null,
        long? offset = null,
        long? length = null,
        string? value = null)
    {
        var item = new DataItemRequest
        {
            Path = NcLinkProtocolPaths.File,
            Operation = operation,
            Keys = key is null ? null : [key],
            Offset = offset,
            Length = length,
            Value = value
        };

        return await _writer.SetItemAsync(deviceId, item, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    private static void ValidateBlockSize(int blockSize)
    {
        if (blockSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(blockSize), blockSize, "分块大小必须为正。");

        if (blockSize > FileTransferLimits.MaxBlockSize)
            throw new ArgumentOutOfRangeException(nameof(blockSize), blockSize,
                $"分块大小不能超过 {FileTransferLimits.MaxBlockSize} 字节（协议限制）。");
    }

    /// <summary>字节 → 小写十六进制字符串（协议要求小写）。</summary>
    public static string BytesToHex(ReadOnlySpan<byte> bytes)
        => bytes.Length == 0 ? string.Empty : Convert.ToHexStringLower(bytes);

    /// <summary>十六进制字符串 → 字节。</summary>
    public static byte[] HexToBytes(string hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return [];
        var trimmed = hex.Trim();
        if (trimmed.Length % 2 != 0)
            throw new FormatException($"十六进制字符串长度必须为偶数，实际 {trimmed.Length}。");

        return Convert.FromHexString(trimmed);
    }

    #endregion

}

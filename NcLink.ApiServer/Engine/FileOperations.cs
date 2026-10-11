using Microsoft.Extensions.Logging;
using NcLink.ApiServer.General.Logging;
using NcLink.ApiServer.Models;
using NcLink.Client.Files.Interfaces;

namespace NcLink.ApiServer.Engine;

/// <summary>
/// 文件接口的实现，<b>v1 与 v2 控制器共用</b>（两条路由的行为本来就相同）。
///
/// 与数据接口不同的是：<b>成功时不返回 <see cref="DataResponse"/>，而是文件原始字节流</b>
/// （实测官方是 Spring 的 <c>ResponseEntity&lt;Resource&gt;</c>，且不带 <c>Content-Type</c>）；
/// 只有失败才回 JSON。
/// </summary>
internal static class FileOperations
{
    /// <summary>
    /// 下载结果：成功给字节，失败给错误响应——两者必有其一。
    /// 用这个而不是返回 <c>null</c>，是为了让"空文件"与"取不到"不会被混为一谈。
    /// </summary>
    public readonly record struct DownloadResult(byte[]? Bytes, DataResponse? Error)
    {
        public static DownloadResult Ok(byte[] bytes) => new(bytes, null);
        public static DownloadResult Failed(DataResponse error) => new(null, error);
    }

    /// <summary>下载文件。取不到大小（文件不存在）时返回 <c>code 13</c>。</summary>
    public static async Task<DownloadResult> DownloadAsync(
        string deviceId,
        string key,
        int? offset,
        int? length,
        IFileTransfer files, ILogger logger,
        CancellationToken cancellationToken)
    {
        try
        {
            // key 就是文件名（协议里 path 用于定位 FILE 数据项，库按设备 + 文件名取文件）
            var bytes = await files.DownloadAsync(deviceId, key, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            // offset/length 是分块参数：给了就只返回该段
            if (offset is > 0 || length is > 0)
            {
                var from = Math.Max(0, offset ?? 0);
                if (from >= bytes.Length) return DownloadResult.Ok([]);
                var count = Math.Min(length ?? (bytes.Length - from), bytes.Length - from);
                bytes = bytes.AsSpan(from, count).ToArray();
            }

            return DownloadResult.Ok(bytes);
        }
        catch (Exception e)
        {
            // 实测：取不到文件大小时官方返回 code 13 且 value 是消息字符串
            ApiServerLog.LogOperationFailed(logger, deviceId, "fileRequest", e.Message);
            return DownloadResult.Failed(DataResponse.FailFile(v2: false));
        }
    }

    /// <summary>上传文件。<paramref name="v2Message"/> 只影响失败文案（官方 v2 少一个 get）。</summary>
    public static async Task<DataResponse> UploadAsync(
        string deviceId,
        Stream content,
        string fileName,
        bool v2Message,
        IFileTransfer files, ILogger logger,
        CancellationToken cancellationToken)
    {
        try
        {
            using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);

            await files.UploadAsync(deviceId, fileName, buffer.ToArray(), cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            return DataResponse.Ok();
        }
        catch (Exception e)
        {
            ApiServerLog.LogOperationFailed(logger, deviceId, "sendFileToDevice", e.Message);
            return DataResponse.FailFile(v2Message);
        }
    }
}

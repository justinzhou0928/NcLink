using Microsoft.AspNetCore.Mvc;
using NcLink.Client.Files.Interfaces;
using NcLink.ApiServer.General.Logging;
using NcLink.ApiServer.Models;

namespace NcLink.ApiServer.Endpoints;

/// <summary>
/// 文件接口：<c>/v1/{device_id}/file/**</c>（官方 <c>DataApi.fileRequest</c> / <c>sendFileToDevice</c>）。
///
/// 语义经官方 apiServer 实测：
/// <list type="bullet">
/// <item><b><c>path</c> 与 <c>key</c> 都是必填</b>——缺任一个返回 HTTP 400
///   （Swagger 也标了 <c>required=true</c>）。<c>path</c> 是 FILE 数据项路径，
///   <c>key</c> 是文件名；</item>
/// <item>响应是 <b>JSON <c>DataResponse</c></b>，<b>不是</b>原始字节流。
///   失败时 <c>value</c> 是<b>字符串</b>消息，例如设备上取不到文件大小时
///   <c>{"status":"FAILED","code":13,"value":"get the file size error"}</c>。</item>
/// </list>
///
/// 成功形状<b>无法在本环境实测</b>——官方模拟器的 G 代码目录是空的
/// （<c>get_keys</c> 返回 <c>[null]</c>，任何 <c>key</c> 都报 size error）。
/// 这里按协议约定返回十六进制字符串（见 <c>AGENTS.md</c> 的文件传输红线）。
/// </summary>
internal static class FileEndpoints
{
    public static RouteGroupBuilder MapFileApi(this RouteGroupBuilder group)
    {
        group.MapGet("/{*rest}", DownloadAsync).Produces<DataResponse>(200);
        group.MapPost("/{*rest}", UploadAsync).Produces<DataResponse>(200).DisableAntiforgery();

        return group;
    }

    private static async Task<IResult> DownloadAsync(
        string device_id,
        [FromQuery] string? path,
        [FromQuery] string? key,
        [FromQuery] int? offset,
        [FromQuery] int? length,
        HttpContext http,
        IFileTransfer files,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger(nameof(FileEndpoints));

        // 实测：path 与 key 都是必填，缺任一个官方返回 400（Spring 默认错误体）
        if (string.IsNullOrWhiteSpace(path) || key is null)
            return Results.Json(ApiError.BadRequest(http.Request.Path), statusCode: 400);

        try
        {
            // key 就是文件名；path 用于定位 FILE 数据项（库按设备 + 文件名取文件）
            var bytes = await files.DownloadAsync(device_id, key, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            // offset/length 是分块参数：给了就只返回该段
            if (offset is > 0 || length is > 0)
            {
                var from = Math.Max(0, offset ?? 0);
                if (from >= bytes.Length) return Results.Bytes([]);
                var count = Math.Min(length ?? (bytes.Length - from), bytes.Length - from);
                bytes = bytes.AsSpan(from, count).ToArray();
            }

            // 实测：成功时官方直接回文件字节流（Spring 的 ResponseEntity<Resource>），
            // 且不带 Content-Type、不是 DataResponse；只有失败才回 JSON。
            // Results.Bytes 会自作主张设成 application/octet-stream，故手写响应。
            http.Response.ContentType = null;
            await http.Response.Body.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            return Results.Empty;
        }
        catch (Exception e)
        {
            // 实测：取不到文件大小时官方返回 code 13 且 value 是消息字符串
            ApiServerLog.LogOperationFailed(logger, device_id, "fileRequest", e.Message);
            return Results.Json(DataResponse.FailFile(v2: false));
        }
    }

    private static async Task<IResult> UploadAsync(
        string device_id,
        [FromForm] IFormFile value,
        [FromForm] string path,
        [FromForm] string key,
        HttpContext http,
        IFileTransfer files,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger(nameof(FileEndpoints));

        // 表单字段名必须与官方一致：path / key / value（file），上传字段叫 value 不是 file。
        // 三个形参声明为非可空——契约里就是必填，可空会让生成的文档说成选填。
        var fileName = string.IsNullOrWhiteSpace(key) ? value.FileName : key;
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(fileName))
            return Results.Json(ApiError.BadRequest(http.Request.Path), statusCode: 400);

        try
        {
            await using var stream = value.OpenReadStream();
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);

            await files.UploadAsync(device_id, fileName, buffer.ToArray(), cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            return Results.Json(DataResponse.Ok());
        }
        catch (Exception e)
        {
            ApiServerLog.LogOperationFailed(logger, device_id, "sendFileToDevice", e.Message);
            return Results.Json(DataResponse.FailFile(v2: false));
        }
    }
}

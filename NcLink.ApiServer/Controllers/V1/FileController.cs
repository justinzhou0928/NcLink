using Microsoft.AspNetCore.Mvc;
using NcLink.ApiServer.Operations;
using NcLink.ApiServer.Models;
using NcLink.Client.Files.Interfaces;

namespace NcLink.ApiServer.Controllers.V1;

/// <summary>
/// v1 文件接口。
///
/// <b>对外一致</b>：URL、表单字段名（<c>path</c>/<c>key</c>/<c>value</c>）、
/// 响应形状（成功回文件原始字节流、失败回 JSON）都与官方对齐。
///
/// <b>对内按 .NET 标准</b>：与 <see cref="DataController"/> 分开——
/// 两者依赖不同（这里只有 <see cref="IFileTransfer"/>），混在一个类里
/// 只会让每个控制器都背着用不上的依赖。厂家把数据和文件塞在同一个
/// <c>DataApi</c> 里，是 Java 控制器按类级前缀组织的副作用，不必照搬。
/// </summary>
[ApiController]
[Route("/v1")]
public sealed class FileController(
    IFileTransfer files,
    ILogger<FileController> logger) : ControllerBase
{
    /// <summary>
    /// 文件下载。成功时回<b>文件原始字节流</b>（不是 <c>DataResponse</c>），失败才回 JSON。
    /// </summary>
    [HttpGet("{deviceId}/file/{**rest}")]
    public async Task<IActionResult> FileRequestAsync(
        string deviceId,
        [FromQuery] string? path,
        [FromQuery] string? key,
        [FromQuery] int? offset,
        [FromQuery] int? length,
        CancellationToken cancellationToken)
    {
        // 实测：path 与 key 都是必填，缺任一个官方返回 400（Spring 默认错误体）
        if (string.IsNullOrWhiteSpace(path) || key is null)
            return StatusCode(400, ApiError.BadRequest(Request.Path));

        var result = await FileOperations.DownloadAsync(
            deviceId, key, offset, length, files, logger, cancellationToken).ConfigureAwait(false);

        if (result.Error is not null) return Ok(result.Error);

        // 实测：成功时官方不带 Content-Type（Spring 的 ResponseEntity<Resource> 就是这么发的）
        Response.ContentType = null;
        await Response.Body.WriteAsync(result.Bytes!, cancellationToken).ConfigureAwait(false);
        return new EmptyResult();
    }

    /// <summary>文件上传（multipart）。表单字段名 <c>path</c> / <c>key</c> / <c>value</c> 与官方一致。</summary>
    [HttpPost("{deviceId}/file/{**rest}")]
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> SendFileToDeviceAsync(
        string deviceId,
        [FromForm] IFormFile? value,
        [FromForm] string? path,
        [FromForm] string? key,
        CancellationToken cancellationToken)
    {
        // 上传的文件字段叫 value，不是 file——用错名字官方表单会打不中这里
        var fileName = string.IsNullOrWhiteSpace(key) ? value?.FileName : key;

        if (value is null || string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(fileName))
            return StatusCode(400, ApiError.BadRequest(Request.Path));

        await using var stream = value.OpenReadStream();
        var response = await FileOperations.UploadAsync(
            deviceId, stream, fileName, v2Message: false, files, logger, cancellationToken).ConfigureAwait(false);

        return Ok(response);
    }
}

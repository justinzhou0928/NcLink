using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using NcLink.ApiServer.Engine;
using NcLink.ApiServer.Models;
using NcLink.Client.Files.Interfaces;
using NcLink.Client.Query.Interfaces;
using NcLink.Client.Set.Interfaces;

namespace NcLink.ApiServer.Controllers.V1;

/// <summary>
/// v1 数据与文件接口，对应厂家的
/// <c>cn.nerc.nclink.apiserver.controller.v1.DataApi</c>。
///
/// <b>类级路由与方法路由刻意与厂家一一对应</b>（厂家类上写
/// <c>@RequestMapping("/v1")</c>，方法上写 <c>@RequestMapping("/{device_id}/data/**")</c>），
/// 这样两份代码可以并排看。控制器只做路由与协议编解码，逻辑在
/// <see cref="DataOperations"/> / <see cref="FileOperations"/>。
/// </summary>
[ApiController]
[Route("/v1")]
public sealed class DataController(
    IQuery query,
    ISet set,
    IFileTransfer files,
    ILogger<DataController> logger) : ControllerBase
{
    /// <summary>数据接口（POST）：请求体是一个 <c>DataRequest</c>。</summary>
    [HttpPost("{device_id}/data/{**rest}")]
    public async Task<DataResponse> DataRequestAsync(
        string device_id,
        [FromBody] DataRequest? request,
        CancellationToken cancellationToken)
        => await DataOperations.ExecuteAsync(
            device_id, request, query, set, logger, cancellationToken).ConfigureAwait(false);

    /// <summary>数据接口（GET）：从查询参数拼数据项（官方此形式不可用，见 API-SERVER 文档）。</summary>
    [HttpGet("{device_id}/data/{**rest}")]
    public async Task<DataResponse> DataRequestByQueryAsync(
        string device_id,
        [FromQuery] string? operation,
        [FromQuery] string? path,
        [FromQuery] int[]? index,
        [FromQuery] string[]? key,
        [FromQuery] int? offset,
        [FromQuery] string? value,
        CancellationToken cancellationToken)
        => await DataOperations.ExecuteGetAsync(
            device_id, operation, path, index, key, offset, value,
            query, set, logger, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// 文件下载。成功时回<b>文件原始字节流</b>（不是 <c>DataResponse</c>），失败才回 JSON。
    /// </summary>
    [HttpGet("{device_id}/file/{**rest}")]
    public async Task<IActionResult> FileRequestAsync(
        string device_id,
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
            device_id, key, offset, length, files, logger, cancellationToken).ConfigureAwait(false);

        if (result.Error is not null) return Ok(result.Error);

        // 实测：成功时官方不带 Content-Type（Spring 的 ResponseEntity<Resource> 就是这么发的）
        Response.ContentType = null;
        await Response.Body.WriteAsync(result.Bytes!, cancellationToken).ConfigureAwait(false);
        return new EmptyResult();
    }

    /// <summary>
    /// 文件上传（multipart）。表单字段名必须与官方一致：<c>path</c> / <c>key</c> / <c>value</c>。
    /// </summary>
    [HttpPost("{device_id}/file/{**rest}")]
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> SendFileToDeviceAsync(
        string device_id,
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
            device_id, stream, fileName, v2Message: false, files, logger, cancellationToken).ConfigureAwait(false);

        return Ok(response);
    }
}

using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using NcLink.ApiServer.Operations;
using NcLink.ApiServer.Models;
using NcLink.Client.Files.Interfaces;

namespace NcLink.ApiServer.Controllers.V2;

/// <summary>
/// v2 文件接口。
///
/// <b>对外一致</b>：与 v1 的形状相同（官方两套控制器的对外形状本来就一样）。
///
/// 官方这个方法也是坏的（<c>requestFile2</c> 走 <c>SyncClient2</c>，同 v2 数据接口的根因）。
/// <b>我们默认复用 v1 的正确实现</b>；<see cref="ApiServerOptions.ReproduceOfficialV2Defects"/>
/// 打开时改为复刻官方缺陷。
/// </summary>
[ApiController]
[Route("/v2")]
public sealed class FileController(
    IFileTransfer files,
    IOptions<ApiServerOptions> options,
    ILogger<FileController> logger) : ControllerBase
{
    private bool ReproduceDefects => options.Value.ReproduceOfficialV2Defects;

    /// <summary>文件下载。</summary>
    [HttpGet("{deviceId}/file/{**rest}")]
    public async Task<IActionResult> FileRequestAsync(
        string deviceId,
        [FromQuery] string? path,
        [FromQuery] string? key,
        [FromQuery] int? offset,
        [FromQuery] int? length,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path) || key is null)
            return StatusCode(400, ApiError.BadRequest(Request.Path));

        if (ReproduceDefects)
        {
            // 官方 v2 的文件下载同样走 SyncClient2，恒定 code 13（实测约 5030 ms）
            return Ok(DataResponse.FailFile(v2: true));
        }

        var result = await FileOperations.DownloadAsync(
            deviceId, key, offset, length, files, logger, cancellationToken).ConfigureAwait(false);

        if (result.Error is not null) return Ok(result.Error);

        Response.ContentType = null;
        await Response.Body.WriteAsync(result.Bytes!, cancellationToken).ConfigureAwait(false);
        return new EmptyResult();
    }

    /// <summary>文件上传（multipart）。</summary>
    [HttpPost("{deviceId}/file/{**rest}")]
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> SendFileToDeviceAsync(
        string deviceId,
        [FromForm] IFormFile? value,
        [FromForm] string? path,
        [FromForm] string? key,
        CancellationToken cancellationToken)
    {
        var fileName = string.IsNullOrWhiteSpace(key) ? value?.FileName : key;

        if (value is null || string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(fileName))
            return StatusCode(400, ApiError.BadRequest(Request.Path));

        // 官方 v2 独有一条魔数校验：key 长度 ≤ 3 直接拒（v1 没有这条）。
        // 只在复刻模式下还原——它没有依据，不应作为我们的行为。
        if (ReproduceDefects && fileName.Length <= 3)
            return Ok(DataResponse.Fail(DataResponse.CodeExecuteDevice, "params error"));

        await using var stream = value.OpenReadStream();
        var response = await FileOperations.UploadAsync(
            deviceId, stream, fileName, v2Message: true, files, logger, cancellationToken).ConfigureAwait(false);

        return Ok(response);
    }
}

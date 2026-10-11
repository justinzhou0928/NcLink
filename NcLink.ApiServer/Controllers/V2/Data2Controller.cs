using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using NcLink.ApiServer.Engine;
using NcLink.ApiServer.Models;
using NcLink.Client.Files.Interfaces;
using NcLink.Client.Query.Interfaces;
using NcLink.Client.Set.Interfaces;

namespace NcLink.ApiServer.Controllers.V2;

/// <summary>
/// v2 数据与文件接口，对应厂家的
/// <c>cn.nerc.nclink.apiserver.controller.v2.DataApi2</c>。
///
/// <b>官方这个类里的每个方法都是坏的</b>（根因见 API-SERVER 文档：
/// <c>SyncClient2</c> 把请求发到四层主题 <c>Query/Request/{SN}/{clientId}</c>，
/// 而设备只订阅三层）。我们因此让本控制器<b>默认复用 v1 的正确实现</b>——
/// 两条路由的参数与响应形状本来就相同。
///
/// <see cref="ApiServerOptions.ReproduceOfficialV2Defects"/> 打开时，
/// 改为逐项复刻官方缺陷，用于向厂家证明"我们完整理解你的实现"。
/// </summary>
[ApiController]
[Route("/v2")]
public sealed class Data2Controller(
    IQuery query,
    ISet set,
    IFileTransfer files,
    IOptions<ApiServerOptions> options,
    ILogger<Data2Controller> logger) : ControllerBase
{
    private bool ReproduceDefects => options.Value.ReproduceOfficialV2Defects;

    /// <summary>数据接口（POST）。</summary>
    [HttpPost("{device_id}/data/{**rest}")]
    public async Task<DataResponse> DataRequestAsync(
        string device_id,
        [FromBody] DataRequest? request,
        CancellationToken cancellationToken)
    {
        if (ReproduceDefects)
            return DataOperations.ExecuteWithOfficialV2Defects(request, "POST", device_id, logger);

        return await DataOperations.ExecuteAsync(
            device_id, request, query, set, logger, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>数据接口（GET）。</summary>
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
    {
        if (ReproduceDefects)
            return DataOperations.ExecuteWithOfficialV2Defects(null, "GET", device_id, logger);

        return await DataOperations.ExecuteGetAsync(
            device_id, operation, path, index, key, offset, value,
            query, set, logger, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>文件下载。</summary>
    [HttpGet("{device_id}/file/{**rest}")]
    public async Task<IActionResult> FileRequestAsync(
        string device_id,
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
            device_id, key, offset, length, files, logger, cancellationToken).ConfigureAwait(false);

        if (result.Error is not null) return Ok(result.Error);

        Response.ContentType = null;
        await Response.Body.WriteAsync(result.Bytes!, cancellationToken).ConfigureAwait(false);
        return new EmptyResult();
    }

    /// <summary>文件上传（multipart）。</summary>
    [HttpPost("{device_id}/file/{**rest}")]
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> SendFileToDeviceAsync(
        string device_id,
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
            device_id, stream, fileName, v2Message: true, files, logger, cancellationToken).ConfigureAwait(false);

        return Ok(response);
    }
}

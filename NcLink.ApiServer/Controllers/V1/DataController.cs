using Microsoft.AspNetCore.Mvc;
using NcLink.ApiServer.Operations;
using NcLink.ApiServer.Models;
using NcLink.Client.Query.Interfaces;
using NcLink.Client.Set.Interfaces;

namespace NcLink.ApiServer.Controllers.V1;

/// <summary>
/// v1 数据接口。
///
/// <b>对外一致</b>：URL、HTTP 方法、参数位置、响应形状、错误码都与官方
/// <c>controller/v1/DataApi</c> 对齐，因此可以直接替换掉它。
///
/// <b>对内按 .NET 标准</b>：控制器只做路由与协议编解码，逻辑在
/// <see cref="DataOperations"/>；文件接口在 <see cref="FileController"/>
/// （两者依赖不同，混在一起会让每个控制器背着用不上的依赖）。
/// </summary>
[ApiController]
[Route("/v1")]
public sealed class DataController(
    IQuery query,
    ISet set,
    ILogger<DataController> logger) : ControllerBase
{
    /// <summary>数据接口（POST）：请求体是一个 <c>DataRequest</c>。</summary>
    [HttpPost("{deviceId}/data/{**rest}")]
    public async Task<DataResponse> DataRequestAsync(
        string deviceId,
        [FromBody] DataRequest? request,
        CancellationToken cancellationToken)
        => await DataOperations.ExecuteAsync(
            deviceId, request, query, set, logger, cancellationToken).ConfigureAwait(false);

    /// <summary>数据接口（GET）：从查询参数拼数据项（官方此形式不可用，见 API-SERVER 文档）。</summary>
    [HttpGet("{deviceId}/data/{**rest}")]
    public async Task<DataResponse> DataRequestByQueryAsync(
        string deviceId,
        [FromQuery] string? operation,
        [FromQuery] string? path,
        [FromQuery] int[]? index,
        [FromQuery] string[]? key,
        [FromQuery] int? offset,
        [FromQuery] string? value,
        CancellationToken cancellationToken)
        => await DataOperations.ExecuteGetAsync(
            deviceId, operation, path, index, key, offset, value,
            query, set, logger, cancellationToken).ConfigureAwait(false);
}

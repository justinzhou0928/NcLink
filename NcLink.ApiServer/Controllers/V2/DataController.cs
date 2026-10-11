using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using NcLink.ApiServer.Engine;
using NcLink.ApiServer.Models;
using NcLink.Client.Query.Interfaces;
using NcLink.Client.Set.Interfaces;

namespace NcLink.ApiServer.Controllers.V2;

/// <summary>
/// v2 数据接口。
///
/// <b>对外一致</b>：URL、方法、参数位置、响应形状与 v1 相同（官方两套控制器的
/// 对外形状本来就一样），因此可以与官方 <c>controller/v2/DataApi2</c> 直接替换。
///
/// <b>官方这个类里的每个方法都是坏的</b>（根因见 API-SERVER 文档：
/// <c>SyncClient2</c> 把请求发到四层主题 <c>Query/Request/{SN}/{clientId}</c>，
/// 而设备只订阅三层）。<b>我们让 v2 默认复用 v1 的正确实现</b>——
/// 对外形状相同就不该有两份逻辑，这也是官方把 v2 写坏之后又没人发现的根源。
///
/// <see cref="ApiServerOptions.ReproduceOfficialV2Defects"/> 打开时，
/// 改为逐项复刻官方缺陷，用于向厂家证明"我们完整理解你的实现"。
/// </summary>
[ApiController]
[Route("/v2")]
public sealed class DataController(
    IQuery query,
    ISet set,
    IOptions<ApiServerOptions> options,
    ILogger<DataController> logger) : ControllerBase
{
    private bool ReproduceDefects => options.Value.ReproduceOfficialV2Defects;

    /// <summary>数据接口（POST）。</summary>
    [HttpPost("{deviceId}/data/{**rest}")]
    public async Task<DataResponse> DataRequestAsync(
        string deviceId,
        [FromBody] DataRequest? request,
        CancellationToken cancellationToken)
    {
        if (ReproduceDefects)
            return DataOperations.ExecuteWithOfficialV2Defects(request, "POST", deviceId, logger);

        return await DataOperations.ExecuteAsync(
            deviceId, request, query, set, logger, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>数据接口（GET）。</summary>
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
    {
        if (ReproduceDefects)
            return DataOperations.ExecuteWithOfficialV2Defects(null, "GET", deviceId, logger);

        return await DataOperations.ExecuteGetAsync(
            deviceId, operation, path, index, key, offset, value,
            query, set, logger, cancellationToken).ConfigureAwait(false);
    }
}

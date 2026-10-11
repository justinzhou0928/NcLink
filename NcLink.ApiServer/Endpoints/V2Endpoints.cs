using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using NcLink.ApiServer.General.Logging;
using NcLink.ApiServer.Models;
using NcLink.Client.Files.Interfaces;
using NcLink.Client.Query.Interfaces;
using NcLink.Client.Set.Interfaces;

namespace NcLink.ApiServer.Endpoints;

/// <summary>
/// v2 端点：<c>/v2/{device_id}/...</c>。
///
/// <b>官方 0.3.10 的 v2 是系统性坏掉的。</b>根因在反编译
/// <c>nclink-core-2.1.3</c> 后已确证，不再是推测：
///
/// <list type="number">
/// <item><c>SyncClient2</c> 把请求发到 <c>Query/Request/{SN}/{clientId}</c>（四层），
///   而设备只订阅三层 <c>Query/Request/{SN}</c> —— 请求<b>发到了没人收的主题上</b>，
///   于是永远等不到响应。<c>SyncClient</c>（v1）用的才是正确的三层。</item>
/// <item><c>InteractionManager.execute2</c> 只等 <b>1000 ms</b>（v1 的 <c>execute</c> 等 4000 ms），
///   所以失败来得更快，表现为恒定 <c>code 16</c>。</item>
/// <item>参数错误被 <c>case 1..5:</c> 全部塌成 <c>code 5</c>，丢失了具体原因。</item>
/// </list>
///
/// <b>我们的立场：官方正确处对齐，坏处修好。</b>因此默认走与 v1 同一条正确路径
/// （v2 与 v1 的参数、响应形状本来就相同），并返回精确错误码。
/// 需要向厂家证明"我们完整复刻了你的实现"时，打开
/// <see cref="ApiServerOptions.ReproduceOfficialV2Defects"/>。
/// </summary>
internal static class V2Endpoints
{
    /// <summary>
    /// 官方把 <c>code 1..5</c> 全部塌成 5 用的就是这个值
    /// （<c>StatusCode.REQUEST_ERR_KEY</c>）。
    /// </summary>
    private const int OfficialV2CollapsedCode = 5;

    /// <summary>v2 数据接口（前缀由调用方给）。</summary>
    public static RouteGroupBuilder MapDataApi2(this RouteGroupBuilder group)
    {
        // GET 不能有请求体——必须与 POST 分开，否则 Minimal API 的元数据推断会报
        // "Body was inferred but the method does not allow inferred body parameters"。
        group.MapPost("/{*rest}", HandleDataPostAsync).Produces<DataResponse>(200);
        group.MapGet("/{*rest}", HandleDataGetAsync).Produces<DataResponse>(200);
        return group;
    }

    /// <summary>v2 文件接口（前缀由调用方给）。</summary>
    public static RouteGroupBuilder MapFileApi2(this RouteGroupBuilder group)
    {
        group.MapGet("/{*rest}", DownloadAsync).Produces<DataResponse>(200);
        group.MapPost("/{*rest}", UploadAsync).Produces<DataResponse>(200).DisableAntiforgery();
        return group;
    }

    /// <summary>v2 模型接口（前缀由调用方给）。</summary>
    public static RouteGroupBuilder MapModelApi2(this RouteGroupBuilder group)
    {
        group.MapGet("", GetModelAsync).Produces<DataResponse>(200);
        group.MapPost("", SetModelAsync).Produces<DataResponse>(200);
        return group;
    }

    #region 数据

    private static async Task<IResult> HandleDataPostAsync(
        string device_id,
        [FromBody] DataRequest? request,
        IQuery query,
        ISet set,
        IOptions<ApiServerOptions> options,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger(nameof(V2Endpoints));

        if (options.Value.ReproduceOfficialV2Defects)
            return ReproduceData(request, logger, device_id, "POST");

        // 默认：与 v1 同一条正确路径，错误码也精确（不再塌成 5）
        return await DataEndpoints.ExecuteRequestAsync(
            device_id, request, query, set, logger, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IResult> HandleDataGetAsync(
        string device_id,
        [FromQuery] string? operation,
        [FromQuery] string? path,
        [FromQuery] int[]? index,
        [FromQuery] string[]? key,
        [FromQuery] int? offset,
        [FromQuery] string? value,
        IQuery query,
        ISet set,
        IOptions<ApiServerOptions> options,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger(nameof(V2Endpoints));

        if (options.Value.ReproduceOfficialV2Defects)
            return ReproduceData(null, logger, device_id, "GET");

        return await DataEndpoints.ExecuteGetRequestAsync(
            device_id, operation, path, index, key, offset, value, query, set,
            logger, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 复刻官方 v2 数据接口的缺陷：参数问题一律 <c>code 5</c>，
    /// 参数合法则恒定 <c>code 16</c>（官方那边真等 1000 ms 超时，我们立即返回）。
    /// </summary>
    private static IResult ReproduceData(DataRequest? request, ILogger logger, string deviceId, string method)
    {
        if (request?.Items is { Count: > 0 })
        {
            var operation = request.Operation ?? "get_value";
            var allValid = NcLink.Protocol.Contracts.NcLinkOperationNames.TryParse(operation, out _)
                && request.Items.All(i => Engine.DataRequestMapper.ToDataItemRequest(i, operation, out _, out _) is not null);

            if (allValid)
            {
                ApiServerLog.LogV2Call(logger, method, deviceId, DataResponse.CodeExecuteTimeout);
                return Results.Json(DataResponse.Fail(DataResponse.CodeExecuteTimeout));
            }
        }
        else if (request?.Items is { Count: 0 })
        {
            // items: [] 官方仍返回成功空数组
            return Results.Json(DataResponse.Ok(Array.Empty<object>()));
        }

        ApiServerLog.LogV2Call(logger, method, deviceId, OfficialV2CollapsedCode);
        return Results.Json(DataResponse.Fail(OfficialV2CollapsedCode));
    }

    #endregion

    #region 文件

    private static async Task<IResult> DownloadAsync(
        string device_id,
        [FromQuery] string? path,
        [FromQuery] string? key,
        [FromQuery] int? offset,
        [FromQuery] int? length,
        HttpContext http,
        IFileTransfer files,
        IOptions<ApiServerOptions> options,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path) || key is null)
            return Results.Json(ApiError.BadRequest(http.Request.Path), statusCode: 400);

        var logger = loggerFactory.CreateLogger(nameof(V2Endpoints));

        if (options.Value.ReproduceOfficialV2Defects)
        {
            // 官方 v2 的文件下载同样走 SyncClient2，恒定 code 13（实测约 5030 ms）
            ApiServerLog.LogOperationFailed(logger, device_id, "v2 fileRequest", "复刻官方 v2 缺陷");
            return Results.Json(DataResponse.FailFile(v2: true));
        }

        return await FileEndpoints.ExecuteDownloadAsync(
            device_id, path, key, offset, length, http, files, logger, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IResult> UploadAsync(
        string device_id,
        [FromForm] IFormFile value,
        [FromForm] string path,
        [FromForm] string key,
        HttpContext http,
        IFileTransfer files,
        IOptions<ApiServerOptions> options,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger(nameof(V2Endpoints));

        // 三个形参声明为非可空：契约里就是必填，写成可空会让生成的文档说成选填
        var fileName = string.IsNullOrWhiteSpace(key) ? value.FileName : key;

        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(fileName))
            return Results.Json(ApiError.BadRequest(http.Request.Path), statusCode: 400);

        // 官方 v2 独有一条魔数校验：key 长度 ≤ 3 直接拒（v1 没有这条）。
        // 只在复刻模式下还原——它没有依据，不应作为我们的行为。
        if (options.Value.ReproduceOfficialV2Defects && fileName.Length <= 3)
        {
            ApiServerLog.LogBadDataRequest(logger, $"复刻官方 v2：key 长度 {fileName.Length} ≤ 3");
            return Results.Json(DataResponse.Fail(DataResponse.CodeExecuteDevice, "params error"));
        }

        return await FileEndpoints.ExecuteUploadAsync(
            device_id, value, fileName, http, files, logger, v2Message: true, cancellationToken)
            .ConfigureAwait(false);
    }

    #endregion

    #region 模型

    /// <summary>v2 取模型：实测与 v1 逐字节相同。</summary>
    private static async Task<IResult> GetModelAsync(
        string device_id,
        IQuery query,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger(nameof(V2Endpoints));

        try
        {
            var model = await query.GetModelAsync(device_id, refresh: false, cancellationToken)
                .ConfigureAwait(false);

            var reshaped = Engine.ModelReshaper.Reshape(model.RawJson);
            if (reshaped is null)
                return Results.Json(DataResponse.Fail(DataResponse.CodeRequestModel));

            return Results.Content(
                "{\"status\":\"SUCCESS\",\"code\":0,\"value\":" + Engine.ModelReshaper.ToJson(reshaped) + "}",
                "application/json; charset=utf-8");
        }
        catch (Exception e)
        {
            ApiServerLog.LogOperationFailed(logger, device_id, "v2 getModel", e.Message);
            return Results.Json(DataResponse.Fail(DataResponse.CodeExecuteFailed));
        }
    }

    private static async Task<IResult> SetModelAsync(
        string device_id,
        [FromBody] System.Text.Json.JsonElement model,
        IQuery query,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger(nameof(V2Endpoints));

        try
        {
            if (!NcLink.Client.Devices.Parsers.DeviceModelParser.TryParse(
                    model.GetRawText(), device_id, out var parsed, out var error))
            {
                ApiServerLog.LogBadDataRequest(logger, $"模型解析失败：{error}");
                return Results.Json(DataResponse.Fail(OfficialV2CollapsedCode));
            }

            query.RegisterModel(parsed!);
            return Results.Json(DataResponse.Ok());
        }
        catch (Exception e)
        {
            ApiServerLog.LogOperationFailed(logger, device_id, "v2 setModel", e.Message);
            return Results.Json(DataResponse.Fail(DataResponse.CodeExecuteFailed));
        }
    }

    #endregion
}

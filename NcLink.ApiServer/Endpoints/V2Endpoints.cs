using Microsoft.AspNetCore.Mvc;
using NcLink.ApiServer.General.Logging;
using NcLink.ApiServer.Models;

namespace NcLink.ApiServer.Endpoints;

/// <summary>
/// v2 端点：<c>/v2/{device_id}/...</c>（官方 <c>DataApi2</c> / <c>ModelApi</c>）。
///
/// <b>本文件的存在意义是"忠实复刻实测到的官方行为"，而不是实现一个更好的 v2。</b>
/// 官方 0.3.10 的 v2 控制器实测是系统性退化的（见 docs/api-server.md「v2 的实测行为」）：
///
/// <list type="table">
/// <item><term><c>/v2/{sn}/data/**</c></term>
///   <description>参数错误一律 <c>code 5</c>（v1 会区分 1/2/3/4）；
///     <b>合法请求恒定超时 → <c>code 16</c></b>（20 次全失败，平均 1009 ms）；
///     <c>items: []</c> 仍返回成功空数组。</description></item>
/// <item><term><c>/v2/{sn}/model</c></term>
///   <description>与 v1 <b>逐字节相同</b>（实测 12508 字符一致），但官方 Swagger 未文档化它。</description></item>
/// <item><term><c>/v2/{sn}/file/**</c></term>
///   <description>参数校验同 v1（缺 path/key → 400）；取不到文件时
///     <b>消息文本是 <c>"the file size error"</c></b>（v1 是 <c>"get the file size error"</c>）。</description></item>
/// <item><term><c>/v2/sample/**</c></term>
///   <description><b>不存在</b>（404）。这里因此也不映射。</description></item>
/// </list>
///
/// 差异说明：我们<b>立即</b>返回 <c>code 16</c>，不像官方那样真等一个超时窗口
/// （1009 ms）。对客户端而言 status/code/value 完全一致，只是快得多。
/// </summary>
internal static class V2Endpoints
{
    /// <summary>v2 数据接口（前缀由调用方给）。</summary>
    public static RouteGroupBuilder MapDataApi2(this RouteGroupBuilder group)
    {
        // GET 不能有请求体——必须与 POST 分开，否则 Minimal API 的元数据推断会报
        // "Body was inferred but the method does not allow inferred body parameters"。
        group.MapPost("/{*rest}", HandleDataPostAsync).Produces<DataResponse>(200);
        group.MapGet("/{*rest}", HandleDataGet).Produces<DataResponse>(200);
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

    /// <summary>实测：v2 的 GET 形式完全不可用——任何 query 组合都是 <c>code 5</c>。</summary>
    private static IResult HandleDataGet(string device_id, ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger(nameof(V2Endpoints));
        ApiServerLog.LogV2Call(logger, "GET", device_id, DataResponse.CodeV2BadRequest);
        return Results.Json(DataResponse.Fail(DataResponse.CodeV2BadRequest));
    }

    /// <summary>
    /// v2 数据接口（POST）：参数合法 → <c>code 16</c>；参数不合法 → <c>code 5</c>。
    /// </summary>
    private static IResult HandleDataPostAsync(
        string device_id,
        [FromBody] DataRequest? request,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger(nameof(V2Endpoints));

        // 参数校验：v2 把所有参数问题都归并到 5
        if (request?.Items is null)
        {
            ApiServerLog.LogV2Call(logger, "POST", device_id, DataResponse.CodeV2BadRequest);
            return Results.Json(DataResponse.Fail(DataResponse.CodeV2BadRequest));
        }

        // items: [] 官方仍返回成功空数组，与 v1 相同
        if (request.Items.Count == 0)
            return Results.Json(DataResponse.Ok(Array.Empty<object>()));

        var operation = request.Operation ?? "get_value";
        if (!NcLink.Protocol.Contracts.NcLinkOperationNames.TryParse(operation, out _))
        {
            ApiServerLog.LogV2Call(logger, "POST", device_id, DataResponse.CodeV2BadRequest);
            return Results.Json(DataResponse.Fail(DataResponse.CodeV2BadRequest));
        }

        foreach (var item in request.Items)
        {
            var mapped = Engine.DataRequestMapper.ToDataItemRequest(item, operation, out _, out _);
            if (mapped is null)
            {
                ApiServerLog.LogV2Call(logger, "POST", device_id, DataResponse.CodeV2BadRequest);
                return Results.Json(DataResponse.Fail(DataResponse.CodeV2BadRequest));
            }
        }

        // 参数都合法 —— 官方在这里等满超时窗口后返回 16
        ApiServerLog.LogV2Call(logger, "POST", device_id, DataResponse.CodeV2DataTimeout);
        return Results.Json(DataResponse.Fail(DataResponse.CodeV2DataTimeout));
    }

    /// <summary>v2 file 的下载：校验与 v1 相同，仅错误消息文本不同。</summary>
    /// <summary>
    /// v2 的文件下载：与 v2 的数据接口一样是<b>退化</b>的。
    ///
    /// 实测（文件确实存在、重复 3 次）：官方恒定返回 <c>code 13</c>
    /// （<c>"the file size error"</c>），耗时约 <b>5030 ms</b>——与 v2 数据接口
    /// 恒定 1009 ms 的超时同源。文件不存在时同样 13。
    ///
    /// 因此这里<b>忠实复刻</b>：参数校验照旧（缺 path/key → 400），
    /// 其余一律 13。不像官方那样真等满 5 秒。
    /// </summary>
    private static IResult DownloadAsync(
        string device_id,
        [FromQuery] string? path,
        [FromQuery] string? key,
        HttpContext http,
        ILoggerFactory loggerFactory)
    {
        if (string.IsNullOrWhiteSpace(path) || key is null)
            return Results.Json(ApiError.BadRequest(http.Request.Path), statusCode: 400);

        var logger = loggerFactory.CreateLogger(nameof(V2Endpoints));
        ApiServerLog.LogOperationFailed(logger, device_id, "v2 fileRequest", "v2 文件接口恒定失败（复刻官方）");
        return Results.Json(DataResponse.FailFile(v2: true));
    }

    private static async Task<IResult> UploadAsync(
        string device_id,
        [FromForm] IFormFile value,
        [FromForm] string path,
        [FromForm] string key,
        HttpContext http,
        NcLink.Client.Files.Interfaces.IFileTransfer files,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger(nameof(V2Endpoints));

        // 三个形参声明为非可空：契约里就是必填，写成可空会让生成的文档说成选填
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
            ApiServerLog.LogOperationFailed(logger, device_id, "v2 sendFileToDevice", e.Message);
            return Results.Json(DataResponse.FailFile(v2: true));
        }
    }

    /// <summary>v2 取模型：实测与 v1 完全一致。</summary>
    private static async Task<IResult> GetModelAsync(
        string device_id,
        NcLink.Client.Query.Interfaces.IQuery query,
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
                return Results.Json(DataResponse.Fail(DataResponse.CodeDataFailed));

            return Results.Content(
                "{\"status\":\"SUCCESS\",\"code\":0,\"value\":" + Engine.ModelReshaper.ToJson(reshaped) + "}",
                "application/json; charset=utf-8");
        }
        catch (Exception e)
        {
            ApiServerLog.LogOperationFailed(logger, device_id, "v2 getModel", e.Message);
            return Results.Json(DataResponse.Fail(DataResponse.CodeDataFailed));
        }
    }

    private static async Task<IResult> SetModelAsync(
        string device_id,
        [FromBody] System.Text.Json.JsonElement model,
        NcLink.Client.Query.Interfaces.IQuery query,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger(nameof(V2Endpoints));

        try
        {
            if (!NcLink.Client.Devices.Parsers.DeviceModelParser.TryParse(
                    model.GetRawText(), device_id, out var parsed, out var error))
            {
                ApiServerLog.LogBadDataRequest(logger, $"模型解析失败：{error}");
                return Results.Json(DataResponse.Fail(DataResponse.CodeV2BadRequest));
            }

            query.RegisterModel(parsed!);
            return Results.Json(DataResponse.Ok());
        }
        catch (Exception e)
        {
            ApiServerLog.LogOperationFailed(logger, device_id, "v2 setModel", e.Message);
            return Results.Json(DataResponse.Fail(DataResponse.CodeDataFailed));
        }
    }
}

using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using NcLink.Client.Files.Interfaces;
using NcLink.ApiServer.Engine;
using NcLink.ApiServer.General.Logging;
using NcLink.ApiServer.Models;

namespace NcLink.ApiServer.Endpoints;

/// <summary>
/// 模型接口：<c>/v1/{deviceId}/model</c>（官方 <c>ModelApi</c>）。
///
/// <list type="bullet">
/// <item><c>GET</c> → 取设备模型（对应官方 <c>getModel(deviceId)</c>），
///   <c>value</c> 是模型对象本身；</item>
/// <item><c>POST</c> → 注入模型，跳过探测（对应官方 <c>setModel(deviceId, timeout, RootNode)</c>）。</item>
/// </list>
/// </summary>
internal static class ModelEndpoints
{
    public static RouteGroupBuilder MapModelApi(this RouteGroupBuilder group)
    {
        group.MapGet("", GetModelAsync).Produces<DataResponse>(200);
        group.MapPost("", SetModelAsync).Produces<DataResponse>(200);

        return group;
    }

    private static async Task<IResult> GetModelAsync(
        string device_id,
        NcLink.Client.Query.Interfaces.IQuery query,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger(nameof(ModelEndpoints));

        try
        {
            var model = await query.GetModelAsync(device_id, refresh: false, cancellationToken)
                .ConfigureAwait(false);

            // 官方是把模型解析成 Java POJO 再序列化的，因此输出补了 dataName、
            // 字段顺序也跟着类声明走。这里用 ModelReshaper 复刻该形状
            // （它不是格式问题：dataName 里是 VARIABLE 的 number，丢了就是丢内容）。
            var reshaped = ModelReshaper.Reshape(model.RawJson);
            if (reshaped is null)
                return Results.Json(DataResponse.Fail(DataResponse.CodeDataFailed));

            return Results.Content(
                "{\"status\":\"SUCCESS\",\"code\":0,\"value\":" + ModelReshaper.ToJson(reshaped) + "}",
                "application/json; charset=utf-8");
        }
        catch (Exception e)
        {
            ApiServerLog.LogOperationFailed(logger, device_id, "getModel", e.Message);
            return Results.Json(DataResponse.Fail(DataResponse.CodeDataFailed));
        }
    }

    private static async Task<IResult> SetModelAsync(
        string device_id,
        [FromBody] System.Text.Json.JsonElement model,
        NcLink.Client.Query.Interfaces.IQuery query,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger(nameof(ModelEndpoints));

        try
        {
            var raw = model.GetRawText();
            if (!NcLink.Client.Devices.Parsers.DeviceModelParser.TryParse(raw, device_id, out var parsed, out var error))
            {
                ApiServerLog.LogBadDataRequest(logger, $"模型解析失败：{error}");
                return Results.Json(DataResponse.Fail(DataResponse.CodePathNotFound));
            }

            query.RegisterModel(parsed!);
            return Results.Json(DataResponse.Ok());
        }
        catch (Exception e)
        {
            ApiServerLog.LogOperationFailed(logger, device_id, "setModel", e.Message);
            return Results.Json(DataResponse.Fail(DataResponse.CodeDataFailed));
        }
    }
}

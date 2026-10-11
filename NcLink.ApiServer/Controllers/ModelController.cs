using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using NcLink.ApiServer.Operations;
using NcLink.ApiServer.Logging;
using NcLink.ApiServer.Models;
using NcLink.Client.Query.Interfaces;

namespace NcLink.ApiServer.Controllers;

/// <summary>
/// 模型接口。
///
/// <b>一个类挂 <c>/v1</c> 与 <c>/v2</c> 两个前缀</b>：两个版本的模型接口对外
/// <b>完全相同</b>，按版本拆成两个类只会产生重复实现。挂两个前缀让这件事在代码里自明。
/// </summary>
[ApiController]
[Route("/v1")]
[Route("/v2")]
public sealed class ModelController(
    IQuery query,
    ILogger<ModelController> logger) : ControllerBase
{
    /// <summary>取设备模型。对应官方 <c>getModel(deviceId)</c>。</summary>
    [HttpGet("{deviceId}/model")]
    public async Task<IActionResult> GetModelAsync(string deviceId, CancellationToken cancellationToken)
    {
        try
        {
            var model = await query.GetModelAsync(deviceId, refresh: false, cancellationToken)
                .ConfigureAwait(false);

            // 官方是把模型解析成 Java POJO 再序列化的，因此输出补了 dataName、
            // 字段顺序也跟着类声明走。这里用 ModelReshaper 复刻该形状
            // （它不是格式问题：dataName 里是 VARIABLE 的 number，丢了就是丢内容）。
            var reshaped = ModelReshaper.Reshape(model.RawJson);
            if (reshaped is null)
                return Ok(DataResponse.Fail(DataResponse.CodeRequestModel));

            // 手写 JSON 头部是为了让 value 内联模型原文而不被再次转义/重排
            return Content(
                "{\"status\":\"SUCCESS\",\"code\":0,\"value\":" + ModelReshaper.ToJson(reshaped) + "}",
                "application/json; charset=utf-8");
        }
        catch (Exception e)
        {
            ApiServerLog.LogOperationFailed(logger, deviceId, "getModel", e.Message);
            return Ok(DataResponse.Fail(DataResponse.CodeExecuteFailed));
        }
    }

    /// <summary>注入模型，跳过探测。对应官方 <c>setModel(deviceId, timeout, RootNode)</c>。</summary>
    [HttpPost("{deviceId}/model")]
    [HttpPut("{deviceId}/model")]
    public async Task<IActionResult> SetModelAsync(
        string deviceId,
        [FromBody] JsonElement model,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!NcLink.Client.Devices.Engine.DeviceModelParser.TryParse(
                    model.GetRawText(), deviceId, out var parsed, out var error))
            {
                ApiServerLog.LogBadDataRequest(logger, $"模型解析失败：{error}");
                return Ok(DataResponse.Fail(DataResponse.CodeRequestBadPath));
            }

            query.RegisterModel(parsed!);
            await Task.CompletedTask.ConfigureAwait(false);
            return Ok(DataResponse.Ok());
        }
        catch (Exception e)
        {
            ApiServerLog.LogOperationFailed(logger, deviceId, "setModel", e.Message);
            return Ok(DataResponse.Fail(DataResponse.CodeExecuteFailed));
        }
    }
}

using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using NcLink.ApiServer.Engine;
using NcLink.ApiServer.General.Logging;
using NcLink.ApiServer.Models;
using NcLink.Client.Query.Interfaces;

namespace NcLink.ApiServer.Controllers;

/// <summary>
/// 模型接口，对应厂家的 <c>cn.nerc.nclink.apiserver.controller.ModelApi</c>。
///
/// <b>厂家是同一个类同时挂 <c>/v1</c> 与 <c>/v2</c> 两个前缀</b>
/// （<c>@RequestMapping(value={"/v1", "v2"})</c>）——这正是 v1 与 v2 模型接口
/// <b>逐字节相同</b>的原因。我们此前拆成两个文件，是没看懂这层关系；
/// 现在照厂家做成一个类挂两个前缀，差异原因就在代码里自明了。
/// </summary>
[ApiController]
[Route("/v1")]
[Route("/v2")]
public sealed class ModelController(
    IQuery query,
    ILogger<ModelController> logger) : ControllerBase
{
    /// <summary>取设备模型。对应官方 <c>getModel(deviceId)</c>。</summary>
    [HttpGet("{device_id}/model")]
    public async Task<IActionResult> GetModelAsync(string device_id, CancellationToken cancellationToken)
    {
        try
        {
            var model = await query.GetModelAsync(device_id, refresh: false, cancellationToken)
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
            ApiServerLog.LogOperationFailed(logger, device_id, "getModel", e.Message);
            return Ok(DataResponse.Fail(DataResponse.CodeExecuteFailed));
        }
    }

    /// <summary>注入模型，跳过探测。对应官方 <c>setModel(deviceId, timeout, RootNode)</c>。</summary>
    [HttpPost("{device_id}/model")]
    [HttpPut("{device_id}/model")]
    public async Task<IActionResult> SetModelAsync(
        string device_id,
        [FromBody] JsonElement model,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!NcLink.Client.Devices.Parsers.DeviceModelParser.TryParse(
                    model.GetRawText(), device_id, out var parsed, out var error))
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
            ApiServerLog.LogOperationFailed(logger, device_id, "setModel", e.Message);
            return Ok(DataResponse.Fail(DataResponse.CodeExecuteFailed));
        }
    }
}

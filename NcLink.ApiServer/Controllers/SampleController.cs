using NcLink.ApiServer.Interfaces;
using Microsoft.AspNetCore.Mvc;
using NcLink.ApiServer.Engine;
using NcLink.ApiServer.Models;

namespace NcLink.ApiServer.Controllers;

/// <summary>
/// 采样接口，对应厂家的 <c>cn.nerc.nclink.apiserver.controller.SampleApi</c>。
///
/// <list type="bullet">
/// <item><c>/sub/{channel_id}</c> → 建立订阅，<c>value</c> 是 <c>sub_id</c>（GUID 字符串）；</item>
/// <item><c>/data/{sub_id}</c> → 取走已缓存的帧（drain），<c>value</c> 是帧数组；</item>
/// <item><c>/unsub/{sub_id}</c> → 取消订阅。</item>
/// </list>
///
/// 这一组是<b>服务端特有</b>的：设备是推送式的，缓存只为 HTTP 轮询而存在。
/// 见 <see cref="SampleCache"/>。
///
/// 三个方法都接受 GET 与 POST —— 厂家的注解就是
/// <c>method={RequestMethod.GET, RequestMethod.POST}</c>（三个方法都是），照抄。
/// </summary>
[ApiController]
[Route("/v1/sample")]
public sealed class SampleController(ISampleCache cache) : ControllerBase
{
    [HttpGet("{device_id}/sub/{channel_id}")]
    [HttpPost("{device_id}/sub/{channel_id}")]
    public async Task<DataResponse> SubscribeAsync(
        string device_id, string channel_id, CancellationToken cancellationToken)
    {
        try
        {
            var subId = await cache.SubscribeAsync(device_id, channel_id, cancellationToken).ConfigureAwait(false);
            return DataResponse.Ok(subId);
        }
        catch (Exception)
        {
            return DataResponse.Fail(DataResponse.CodeExecuteFailed);
        }
    }

    [HttpGet("{device_id}/data/{sub_id}")]
    [HttpPost("{device_id}/data/{sub_id}")]
    public DataResponse GetData(string device_id, string sub_id)
    {
        var frames = cache.Drain(sub_id);

        // 订阅不存在 → 实测官方返回 code 17
        return frames is null
            ? DataResponse.Fail(DataResponse.CodeExecuteBadSubId)
            : DataResponse.Ok(frames);
    }

    [HttpGet("{device_id}/unsub/{sub_id}")]
    [HttpPost("{device_id}/unsub/{sub_id}")]
    public async Task<DataResponse> UnsubscribeAsync(string device_id, string sub_id, CancellationToken cancellationToken)
    {
        var removed = await cache.UnsubscribeAsync(sub_id, cancellationToken).ConfigureAwait(false);
        return removed ? DataResponse.Ok() : DataResponse.Fail(DataResponse.CodeExecuteBadSubId);
    }
}

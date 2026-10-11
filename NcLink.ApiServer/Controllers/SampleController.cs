using NcLink.ApiServer.Operations;
using Microsoft.AspNetCore.Mvc;
using NcLink.ApiServer.Models;

namespace NcLink.ApiServer.Controllers;

/// <summary>
/// 采样接口。
///
/// <list type="bullet">
/// <item><c>/sub/{channelId}</c> → 建立订阅，<c>value</c> 是 <c>subId</c>（GUID 字符串）；</item>
/// <item><c>/data/{subId}</c> → 取走已缓存的帧（drain），<c>value</c> 是帧数组；</item>
/// <item><c>/unsub/{subId}</c> → 取消订阅。</item>
/// </list>
///
/// 这一组是<b>服务端特有</b>的：设备是推送式的，缓存只为 HTTP 轮询而存在。
/// 见 <see cref="ISampleCache"/>。
///
/// 三个方法都接受 GET 与 POST——官方三个方法都声明了这两种方法，
/// 属于<b>对外用法</b>的一部分，因此照齐（不是"照抄注解"，是"用得上的调用方式不能少"）。
/// </summary>
[ApiController]
[Route("/v1/sample")]
public sealed class SampleController(ISampleCache cache) : ControllerBase
{
    [HttpGet("{deviceId}/sub/{channelId}")]
    [HttpPost("{deviceId}/sub/{channelId}")]
    public async Task<DataResponse> SubscribeAsync(
        string deviceId, string channelId, CancellationToken cancellationToken)
    {
        try
        {
            var subId = await cache.SubscribeAsync(deviceId, channelId, cancellationToken).ConfigureAwait(false);
            return DataResponse.Ok(subId);
        }
        catch (Exception)
        {
            return DataResponse.Fail(DataResponse.CodeExecuteFailed);
        }
    }

    [HttpGet("{deviceId}/data/{subId}")]
    [HttpPost("{deviceId}/data/{subId}")]
    public DataResponse GetData(string deviceId, string subId)
    {
        var frames = cache.Drain(subId);

        // 订阅不存在 → 实测官方返回 code 17
        return frames is null
            ? DataResponse.Fail(DataResponse.CodeExecuteBadSubId)
            : DataResponse.Ok(frames);
    }

    [HttpGet("{deviceId}/unsub/{subId}")]
    [HttpPost("{deviceId}/unsub/{subId}")]
    public async Task<DataResponse> UnsubscribeAsync(string deviceId, string subId, CancellationToken cancellationToken)
    {
        var removed = await cache.UnsubscribeAsync(subId, cancellationToken).ConfigureAwait(false);
        return removed ? DataResponse.Ok() : DataResponse.Fail(DataResponse.CodeExecuteBadSubId);
    }
}

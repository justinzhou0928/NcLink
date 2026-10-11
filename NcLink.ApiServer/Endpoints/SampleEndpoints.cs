using NcLink.ApiServer.Engine;
using NcLink.ApiServer.Models;

namespace NcLink.ApiServer.Endpoints;

/// <summary>
/// 采样接口：<c>/v1/sample/{device_id}/...</c>（官方 <c>SampleApi</c>）。
///
/// <list type="bullet">
/// <item><c>POST /sub/{channel_id}</c> → 建立订阅，<c>value</c> 是 <c>sub_id</c>（GUID 字符串）；</item>
/// <item><c>GET /data/{sub_id}</c> → 取走已缓存的帧（drain），<c>value</c> 是帧数组；</item>
/// <item><c>POST /unsub/{sub_id}</c> → 取消订阅。</item>
/// </list>
///
/// 这一组是<b>服务端特有</b>的：设备是推送式的，缓存只为 HTTP 轮询而存在。
/// 见 <see cref="SampleCache"/>。
/// </summary>
internal static class SampleEndpoints
{
    public static RouteGroupBuilder MapSampleApi(this RouteGroupBuilder group)
    {
        group.MapPost("/sub/{channel_id}", SubscribeAsync).Produces<DataResponse>(200);
        group.MapGet("/data/{sub_id}", GetData).Produces<DataResponse>(200);
        group.MapPost("/unsub/{sub_id}", UnsubscribeAsync).Produces<DataResponse>(200);

        return group;
    }

    private static async Task<IResult> SubscribeAsync(
        string device_id, string channel_id, SampleCache cache, CancellationToken cancellationToken)
    {
        try
        {
            var subId = await cache.SubscribeAsync(device_id, channel_id, cancellationToken).ConfigureAwait(false);
            return Results.Json(DataResponse.Ok(subId));
        }
        catch (Exception)
        {
            return Results.Json(DataResponse.Fail(DataResponse.CodeExecuteFailed));
        }
    }

    private static IResult GetData(string device_id, string sub_id, SampleCache cache)
    {
        var frames = cache.Drain(sub_id);

        // 订阅不存在 → 实测官方返回 code 17
        return frames is null
            ? Results.Json(DataResponse.Fail(DataResponse.CodeExecuteBadSubId))
            : Results.Json(DataResponse.Ok(frames));
    }

    private static async Task<IResult> UnsubscribeAsync(
        string device_id, string sub_id, SampleCache cache, CancellationToken cancellationToken)
    {
        var removed = await cache.UnsubscribeAsync(sub_id, cancellationToken).ConfigureAwait(false);
        return Results.Json(removed
            ? DataResponse.Ok()
            : DataResponse.Fail(DataResponse.CodeExecuteBadSubId));
    }
}

using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using NcLink.Client.Connection.Exceptions;
using NcLink.Client.Query;
using NcLink.Client.Query.Interfaces;
using NcLink.Client.Set.Interfaces;
using NcLink.ApiServer.Engine;
using NcLink.ApiServer.General.Logging;
using NcLink.ApiServer.Models;

namespace NcLink.ApiServer.Endpoints;

/// <summary>
/// 数据接口：<c>/v1/{device_id}/data/**</c>（官方 <c>DataApi</c>）。
///
/// 请求体形状见 <see cref="DataRequest"/>；响应形状
/// <c>{ "status", "code", "value" }</c> 中的 <c>value</c> 实测规律：
/// <list type="bullet">
/// <item>查询类（<c>get_*</c>）：外层数组，<b>每个 item 一个值数组</b>，无值时该元素为 <c>null</c>。
///   例：<c>[[123,0,0]]</c>、<c>[[512]]</c>、<c>[null]</c></item>
/// <item>设置类（<c>set_value</c>）：外层数组，<b>每个 item 一个布尔</b>。例：<c>[true]</c></item>
/// <item>多个 item：<c>[[123],[0]]</c></item>
/// </list>
/// 失败时仍返回 HTTP 200，靠 <c>status=FAILED</c> 与 <c>code</c> 表达（与官方一致）。
/// </summary>
internal static class DataEndpoints
{
    /// <summary>
    /// 注册数据接口。<b>前缀由调用方给</b>（见 Program.cs 的路由表）——这是官方文档
    /// 推荐的分组写法：<c>app.MapGroup("/v1/{device_id}/data").MapDataApi()</c>，
    /// 这样前缀集中可见，本文件只放处理器。
    /// </summary>
    public static RouteGroupBuilder MapDataApi(this RouteGroupBuilder group)
    {
        // 官方映射是 /v1/{device_id}/data/**，用 catch-all 保持可替换性
        group.MapPost("/{*rest}", HandleAsync).Produces<DataResponse>(200);
        // GET 形式官方也暴露，但 Swagger 的参数里没有 path（path 在 items 内），
        // 因此 GET 在此额外接受 path 查询参数，作为便捷形式
        group.MapGet("/{*rest}", HandleGetAsync).Produces<DataResponse>(200);

        return group;
    }

    private static async Task<IResult> HandleAsync(
        string device_id,
        [FromBody] DataRequest? request,
        IQuery query,
        ISet set,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
        => await ExecuteRequestAsync(
            device_id, request, query, set,
            loggerFactory.CreateLogger(nameof(DataEndpoints)), cancellationToken);

    /// <summary>
    /// 数据请求的实现，<b>v1 与 v2 共用</b>。
    ///
    /// 两条路由的参数与响应形状本来就相同，官方把它们分成两套（且 v2 那套是坏的）——
    /// 我们只保留一份正确实现，v2 直接复用。
    /// </summary>
    internal static async Task<IResult> ExecuteRequestAsync(
        string deviceId,
        DataRequest? request,
        IQuery query,
        ISet set,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (request is null || request.Items is null)
        {
            // 实测：缺 items → code 1
            const string reason = "请求体缺少 items。";
            ApiServerLog.LogBadDataRequest(logger, reason);
            return Results.Json(DataResponse.Fail(DataResponse.CodeRequestItemsMissing));
        }

        // 实测：items 为空数组 → SUCCESS 且 value 为 []
        if (request.Items.Count == 0)
            return Results.Json(DataResponse.Ok(Array.Empty<object>()));

        return await ExecuteAsync(deviceId, request, query, set, logger, cancellationToken);
    }

    /// <summary>GET 便捷形式：<c>?operation=..&amp;path=..&amp;index=0&amp;index=1&amp;key=..</c>。</summary>
    private static async Task<IResult> HandleGetAsync(
        string device_id,
        [FromQuery] string? operation,
        [FromQuery] string? path,
        [FromQuery] int[]? index,
        [FromQuery] string[]? key,
        [FromQuery] int? offset,
        [FromQuery] string? value,
        IQuery query,
        ISet set,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
        => await ExecuteGetRequestAsync(
            device_id, operation, path, index, key, offset, value, query, set,
            loggerFactory.CreateLogger(nameof(DataEndpoints)), cancellationToken);

    /// <summary>
    /// GET 便捷形式的实现，<b>v1 与 v2 共用</b>。
    ///
    /// <b>这是相对官方的改进</b>：官方的 GET 只读请求体，而 GET 带不了体，
    /// 于是任何 query 组合都返回 <c>code 3</c>——GET 形式在官方那边完全不可用。
    /// 我们把查询参数读起来，GET 因此可用。
    /// </summary>
    internal static async Task<IResult> ExecuteGetRequestAsync(
        string deviceId,
        string? operation,
        string? path,
        int[]? index,
        string[]? key,
        int? offset,
        string? value,
        IQuery query,
        ISet set,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            const string reason = "GET 形式必须提供 path 查询参数。";
            ApiServerLog.LogBadDataRequest(logger, reason);
            return Results.Json(DataResponse.Fail(DataResponse.CodeRequestBadPath));
        }

        var item = new DataRequestItem { Path = path, Offset = offset, Key = ToElement(key) };

        if (index is { Length: > 0 })
            item.Index = JsonSerializer.SerializeToElement<object>(index.Length == 1 ? index[0] : index);

        if (value is not null)
            item.Value = JsonSerializer.SerializeToElement(value);

        var request = new DataRequest { Operation = operation, Items = [item] };
        return await ExecuteAsync(deviceId, request, query, set, logger, cancellationToken);
    }

    private static async Task<IResult> ExecuteAsync(
        string deviceId,
        DataRequest request,
        IQuery query,
        ISet set,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        // 实测：operation <b>缺失</b>才默认 get_value；
        // 显式给空串或全空格是"非法 operation"（code 2），不能当成缺失。
        var operation = request.Operation;
        var effectiveOperation = operation ?? NcLink.Protocol.Contracts.NcLinkOperationNames.GetValue;
        var items = new List<NcLink.Client.Query.Models.DataItemRequest>(request.Items!.Count);

        foreach (var httpItem in request.Items)
        {
            var mapped = DataRequestMapper.ToDataItemRequest(httpItem, operation, out var error, out var errorCode);
            if (mapped is null)
            {
                ApiServerLog.LogBadDataRequest(logger, error ?? "参数不合法。");
                return Results.Json(DataResponse.Fail(errorCode));
            }
            items.Add(mapped);
        }

        var timeout = request.Timeout > 0 ? TimeSpan.FromMilliseconds(request.Timeout) : (TimeSpan?)null;

        // 实测：set_value / add / delete 三者都返回"每个 item 一个布尔"
        var isWrite =
            string.Equals(effectiveOperation, NcLink.Protocol.Contracts.NcLinkOperationNames.SetValue, StringComparison.Ordinal)
            || string.Equals(effectiveOperation, NcLink.Protocol.Contracts.NcLinkOperationNames.Add, StringComparison.Ordinal)
            || string.Equals(effectiveOperation, NcLink.Protocol.Contracts.NcLinkOperationNames.Delete, StringComparison.Ordinal);

        // 先确保设备模型可用。官方对未知 SN 会等一个固定的 3 秒探测窗口后返回 19，
        // 说明它也是先"确保设备已知"再发数据请求——判定为设备不存在而不是请求失败。
        try
        {
            await query.GetModelAsync(deviceId, refresh: false, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            ApiServerLog.LogOperationFailed(logger, deviceId, effectiveOperation, e.Message);
            return Results.Json(DataResponse.Fail(DataResponse.CodeExecuteOther));
        }

        try
        {
            if (isWrite)
            {
                var results = await set.SetAsync(deviceId, items, timeout, cancellationToken).ConfigureAwait(false);
                return Results.Json(DataResponse.Ok(results.Select(r => r.Succeeded).ToArray()));
            }

            var queried = await query.QueryAsync(deviceId, items, timeout, cancellationToken).ConfigureAwait(false);
            // 每个 item 一个值数组；无值时为 null
            var value = queried
                .Select(r => r.Values.Count > 0 ? (object)r.Values : null)
                .ToArray();
            return Results.Json(DataResponse.Ok(value));
        }
        catch (NcLinkModelPathNotFoundException e)
        {
            // 实测：path 解析不到 → code 3
            ApiServerLog.LogOperationFailed(logger, deviceId, effectiveOperation, e.Message);
            return Results.Json(DataResponse.Fail(DataResponse.CodeRequestBadPath));
        }
        catch (Exception e)
        {
            ApiServerLog.LogOperationFailed(logger, deviceId, effectiveOperation, e.Message);
            return Results.Json(DataResponse.Fail(DataResponse.CodeExecuteFailed));
        }
    }

    private static JsonElement? ToElement(string[]? values)
        => values is { Length: > 0 }
            ? JsonSerializer.SerializeToElement<object>(values.Length == 1 ? values[0] : values)
            : null;
}

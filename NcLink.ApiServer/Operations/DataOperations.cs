using System.Text.Json;
using Microsoft.Extensions.Logging;
using NcLink.ApiServer.Logging;
using NcLink.ApiServer.Models;
using NcLink.Client.Query.Interfaces;
using NcLink.Client.Set;

namespace NcLink.ApiServer.Operations;

/// <summary>
/// 数据接口的实现，<b>v1 与 v2 控制器共用</b>。
///
/// 官方把 v1/v2 分成两套（<c>DataApi</c> / <c>DataApi2</c>），而 v2 那套是坏掉的。
/// 两条路由的参数与响应形状本来就相同，因此我们只保留一份正确实现，
/// v2 控制器直接复用——控制器只做路由与协议编解码，逻辑在这里。
/// </summary>
internal static class DataOperations
{
    /// <summary>POST 形式：请求体是一个 <see cref="DataRequest"/>。</summary>
    public static async Task<DataResponse> ExecuteAsync(
        string deviceId, DataRequest? request,
        IQuery query, ISet set, ILogger logger, CancellationToken cancellationToken)
    {
        if (request is null || request.Items is null)
        {
            ApiServerLog.LogBadDataRequest(logger, "请求体缺少 items。");
            return DataResponse.Fail(DataResponse.CodeRequestItemsMissing);
        }

        // 实测：items 为空数组 → SUCCESS 且 value 为 []
        if (request.Items.Count == 0)
            return DataResponse.Ok(Array.Empty<object>());

        return await ExecuteItemsAsync(deviceId, request, query, set, logger, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// GET 形式：从查询参数拼出一个数据项。
    ///
    /// <b>这是相对官方的改进</b>：官方的 GET 只读请求体，而 GET 带不了体，
    /// 于是任何 query 组合都返回 <c>code 3</c>——GET 形式在官方那边完全不可用。
    /// </summary>
    public static async Task<DataResponse> ExecuteGetAsync(
        string deviceId,
        string? operation,
        string? path,
        int[]? index,
        string[]? key,
        int? offset,
        string? value,
        IQuery query, ISet set, ILogger logger,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            ApiServerLog.LogBadDataRequest(logger, "GET 形式必须提供 path 查询参数。");
            return DataResponse.Fail(DataResponse.CodeRequestBadPath);
        }

        var item = new DataRequestItem { Path = path, Offset = offset, Key = ToElement(key) };

        if (index is { Length: > 0 })
            item.Index = JsonSerializer.SerializeToElement<object>(index.Length == 1 ? index[0] : index);

        if (value is not null)
            item.Value = JsonSerializer.SerializeToElement(value);

        return await ExecuteItemsAsync(
            deviceId, new DataRequest { Operation = operation, Items = [item] },
            query, set, logger, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 复刻官方 v2 数据接口的缺陷：参数问题一律 <c>code 5</c>，
    /// 参数合法则恒定 <c>code 16</c>（官方那边真等 1000 ms 超时，我们立即返回）。
    ///
    /// 只在 <c>ReproduceOfficialV2Defects</c> 打开时使用，见 API-SERVER 文档。
    /// </summary>
    public static DataResponse ExecuteWithOfficialV2Defects(
        DataRequest? request, string method, string deviceId, ILogger logger)
    {
        if (request?.Items is { Count: 0 })
            return DataResponse.Ok(Array.Empty<object>());

        if (request?.Items is { Count: > 0 })
        {
            var operation = request.Operation ?? NcLink.Protocol.Models.NcLinkOperationNames.GetValue;
            var allValid =
                NcLink.Protocol.Models.NcLinkOperationNames.TryParse(operation, out _)
                && request.Items.All(i => DataRequestMapper.ToDataItemRequest(i, operation, out _, out _) is not null);

            if (allValid)
            {
                ApiServerLog.LogV2Call(logger, method, deviceId, DataResponse.CodeExecuteTimeout);
                return DataResponse.Fail(DataResponse.CodeExecuteTimeout);
            }
        }

        // 官方把 code 1..5 塌成 5（REQUEST_ERR_KEY）
        const int collapsed = DataResponse.CodeRequestBadKey;
        ApiServerLog.LogV2Call(logger, method, deviceId, collapsed);
        return DataResponse.Fail(collapsed);
    }

    private static async Task<DataResponse> ExecuteItemsAsync(
        string deviceId, DataRequest request,
        IQuery query, ISet set, ILogger logger, CancellationToken cancellationToken)
    {
        // 实测：operation <b>缺失</b>才默认 get_value；
        // 显式给空串或全空格是"非法 operation"（code 2），不能当成缺失。
        var operation = request.Operation;
        var effectiveOperation = operation ?? NcLink.Protocol.Models.NcLinkOperationNames.GetValue;
        var items = new List<NcLink.Client.Query.Models.DataItemRequest>(request.Items!.Count);

        foreach (var httpItem in request.Items)
        {
            var mapped = DataRequestMapper.ToDataItemRequest(httpItem, operation, out var error, out var errorCode);
            if (mapped is null)
            {
                ApiServerLog.LogBadDataRequest(logger, error ?? "参数不合法。");
                return DataResponse.Fail(errorCode);
            }
            items.Add(mapped);
        }

        var timeout = request.Timeout > 0 ? TimeSpan.FromMilliseconds(request.Timeout) : (TimeSpan?)null;

        // 实测：set_value / add / delete 三者都返回"每个 item 一个布尔"
        var isWrite =
            string.Equals(effectiveOperation, NcLink.Protocol.Models.NcLinkOperationNames.SetValue, StringComparison.Ordinal)
            || string.Equals(effectiveOperation, NcLink.Protocol.Models.NcLinkOperationNames.Add, StringComparison.Ordinal)
            || string.Equals(effectiveOperation, NcLink.Protocol.Models.NcLinkOperationNames.Delete, StringComparison.Ordinal);

        // 先确保设备模型可用。官方对未知 SN 会等一个固定的 3 秒探测窗口后返回 19，
        // 说明它也是先"确保设备已知"再发数据请求——判定为设备不存在而不是请求失败。
        try
        {
            await query.GetModelAsync(deviceId, refresh: false, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            ApiServerLog.LogOperationFailed(logger, deviceId, effectiveOperation, e.Message);
            return DataResponse.Fail(DataResponse.CodeExecuteOther);
        }

        try
        {
            if (isWrite)
            {
                var results = await set.SetAsync(deviceId, items, timeout, cancellationToken).ConfigureAwait(false);
                return DataResponse.Ok(results.Select(r => r.Succeeded).ToArray());
            }

            var queried = await query.QueryAsync(deviceId, items, timeout, cancellationToken).ConfigureAwait(false);
            // 每个 item 一个值数组；无值时为 null
            var value = queried
                .Select(r => r.Values.Count > 0 ? (object)r.Values : null)
                .ToArray();
            return DataResponse.Ok(value);
        }
        catch (NcLinkModelPathNotFoundException e)
        {
            // 实测：path 解析不到 → code 3
            ApiServerLog.LogOperationFailed(logger, deviceId, effectiveOperation, e.Message);
            return DataResponse.Fail(DataResponse.CodeRequestBadPath);
        }
        catch (Exception e)
        {
            ApiServerLog.LogOperationFailed(logger, deviceId, effectiveOperation, e.Message);
            return DataResponse.Fail(DataResponse.CodeExecuteFailed);
        }
    }

    private static JsonElement? ToElement(string[]? values)
        => values is { Length: > 0 }
            ? JsonSerializer.SerializeToElement<object>(values.Length == 1 ? values[0] : values)
            : null;
}

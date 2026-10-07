using System.Text.Json.Serialization;

namespace NcLink.ApiServer.Models;

/// <summary>
/// 统一响应包装，形状与官方 apiServer 的 <c>DataResponse</c> 一致
/// （取自 Swagger <c>definitions.DataResponse</c>，并经实测核对）：
/// <code>
/// { "status": "SUCCESS", "code": 0, "value": ... }
/// </code>
/// 字段顺序按实测输出（<c>status</c> 在前），但 JSON 顺序不影响解析。
/// </summary>
public sealed class DataResponse
{
    /// <summary>成功。</summary>
    public const int CodeSuccess = 0;

    /// <summary>实测：请求体缺 <c>items</c>（<c>{"operation":"get_value"}</c> 无 items）。</summary>
    public const int CodeItemsMissing = 1;

    /// <summary>实测：<c>operation</c> 不是 7 个合法值之一。</summary>
    public const int CodeBadOperation = 2;

    /// <summary>
    /// 实测：<c>path</c> 缺失，或 <c>path</c> 在设备模型里解析不到。
    /// </summary>
    public const int CodePathNotFound = 3;

    /// <summary>
    /// 实测：参数形状不被接受（例如 <c>index</c> 传区间字符串 <c>"0-7"</c>、
    /// 或数组里含非整数），以及设备调用失败。
    /// </summary>
    public const int CodeDataFailed = 4;

    /// <summary>实测：采样 <c>sub_id</c> 不存在（<c>GET /v1/sample/{sn}/data/{sub}</c>）。</summary>
    public const int CodeSubscriptionNotFound = 17;

    /// <summary>
    /// 实测：文件接口取不到文件大小（<c>GET /v1/{sn}/file/</c>，设备上没有该文件）。
    ///
    /// 注意这一类的 <c>value</c> 是<b>消息字符串</b>而不是 <c>null</c>——
    /// 例如 <c>{"status":"FAILED","code":13,"value":"get the file size error"}</c>。
    /// </summary>
    public const int CodeFileSizeError = 13;

    /// <summary>
    /// 实测：<b>目标对象不存在</b>。两个来源（时延可区分）：
    /// <list type="bullet">
    /// <item><c>key</c> 传空串 → <b>2 ms 内立即返回</b>（本地参数校验）；</item>
    /// <item>未知 SN → <b>约 3012 ms 后返回</b>（固定 3 秒探测窗口，与请求里的
    ///   <c>timeout</c> 无关——把 timeout 设成 1000 或 20000 都是 3012 ms）。</item>
    /// </list>
    /// 因此语义是"目标对象不存在"，而不是"参数非法"或"操作不支持"。
    /// </summary>
    public const int CodeObjectNotFound = 19;

    /// <summary>
    /// 实测：v2 数据接口的<b>所有</b>参数错误都归并到这一个码。
    ///
    /// v1 会区分 1（缺 items）/ 2（operation 非法）/ 3（path 问题）/ 4（参数形状），
    /// v2 一律返回 5 —— 实测缺 items、非法 operation、缺 path、index 传字符串都是 5，
    /// 连 GET 形式（任何 query 组合）也是 5。
    /// </summary>
    public const int CodeV2BadRequest = 5;

    /// <summary>
    /// 实测：<b>v2 数据接口对合法请求恒定超时</b>，返回此码。
    ///
    /// 官方日志给出根因：
    /// <code>
    /// ERROR c.n.n.apiserver.core.InteractionManager : null
    /// java.util.concurrent.TimeoutException: null
    ///     at ...controller.v2.DataApi2.dataRequest(DataApi2.java:67)
    /// </code>
    /// 重复 20 次全部失败（0/20），平均耗时 1009 ms（等满超时窗口）；
    /// 同一设备同一参数的 v1 请求 20/20 成功、平均 10 ms。
    /// 因此这是官方 0.3.10 里 v2 控制器的<b>既有缺陷</b>，不是配置问题。
    /// </summary>
    public const int CodeV2DataTimeout = 16;

    /// <summary>成功状态字面量。</summary>
    public const string StatusSuccess = "SUCCESS";

    /// <summary>失败状态字面量。</summary>
    public const string StatusFailed = "FAILED";

    [JsonPropertyName("status")]
    public string Status { get; set; } = StatusSuccess;

    /// <summary>返回码：<c>0</c> 成功；非 0 失败。</summary>
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("value")]
    public object? Value { get; set; }

    public static DataResponse Ok(object? value = null)
        => new() { Status = StatusSuccess, Code = CodeSuccess, Value = value };

    public static DataResponse Fail(int code, object? value = null)
        => new() { Status = StatusFailed, Code = code, Value = value };

    /// <summary>
    /// 文件接口失败：<c>value</c> 携带<b>固定的</b>消息字符串。
    ///
    /// 实测官方返回的是固定英文文案，不含任何细节：
    /// v1 <c>"get the file size error"</c>、v2 <c>"the file size error"</c>（少一个 get）。
    /// 因此这里也让外部消息恒定，把真实原因留给服务端日志——
    /// 两边逐字符一致，同时不把内部细节泄漏给调用方。
    /// </summary>
    public static DataResponse FailFile(bool v2)
        => new()
        {
            Status = StatusFailed,
            Code = CodeFileSizeError,
            Value = v2 ? "the file size error" : "get the file size error"
        };
}

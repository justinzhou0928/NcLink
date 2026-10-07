using System.Globalization;
using System.Text.Json.Serialization;

namespace NcLink.ApiServer.Models;

/// <summary>
/// Spring Boot 默认错误响应（<c>DefaultErrorAttributes</c>）的形状。
///
/// 实测官方 apiServer 在 HTTP 400 时返回：
/// <code>
/// {"timestamp":"2026-10-07T08:47:32.405+00:00","status":400,
///  "error":"Bad Request","message":"","path":"/v1/STRESS_TEST_00000/file/"}
/// </code>
/// <b>404 与 405 也是同一形状</b>（只是 <c>error</c> 文案不同）：
/// <code>
/// PUT    /v1/{sn}/data/  → 405 {"...","error":"Method Not Allowed",...}
/// GET    /v1/sample/{sn}/data/ → 404 {"...","error":"Not Found",...}
/// </code>
/// 注意这与数据接口的 <see cref="DataResponse"/> 是<b>两套</b>错误表达：
/// 数据接口一律 HTTP 200 + <c>status=FAILED</c>；
/// 而参数绑定失败、路由未命中、方法不允许走 HTTP 4xx + 这个形状。
/// </summary>
public sealed class ApiError
{
    /// <summary>按官方形状构造 400。</summary>
    public static ApiError BadRequest(string path) => Create(400, "Bad Request", path);

    /// <summary>
    /// 按官方形状构造任意错误状态。官方的 <c>/error</c> 由 Spring 统一产出，
    /// 因此所有 4xx/5xx 都是同一形状，只是 <c>error</c> 文案不同。
    /// </summary>
    public static ApiError Create(int status, string error, string path) => new()
    {
        Timestamp = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz", CultureInfo.InvariantCulture),
        Status = status,
        Error = error,
        Message = string.Empty,
        Path = path
    };

    [JsonPropertyName("timestamp")]
    public string Timestamp { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public int Status { get; set; }

    [JsonPropertyName("error")]
    public string Error { get; set; } = string.Empty;

    /// <summary>官方此处是空字符串（不是 null）。</summary>
    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;
}

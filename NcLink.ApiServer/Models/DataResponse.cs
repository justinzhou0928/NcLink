using System.Text.Json.Serialization;

namespace NcLink.ApiServer.Models;

/// <summary>
/// 统一响应包装，形状与官方 apiServer 的 <c>DataResponse</c> 一致
/// （取自 Swagger <c>definitions.DataResponse</c>，并经实测核对）：
/// <code>
/// { "status": "SUCCESS", "code": 0, "value": ... }
/// </code>
///
/// 错误码取自官方核心库反编译出的
/// <c>cn.nerc.nclink.apiserver.entity.StatusCode</c>——
/// <b>这是权威定义，不是实测推断</b>。命名刻意与厂家的常量一一对应，
/// 便于对照：<c>REQUEST_ERR_*</c> 是请求参数问题（1–9），
/// <c>DEVICE_ERR_*</c> 是设备侧问题（10–11），<c>EXECUTE_ERR_*</c> 是执行问题（12–20）。
/// </summary>
public sealed class DataResponse
{
    #region 成功

    /// <summary>厂家 <c>SUCCESS</c>。</summary>
    public const int CodeSuccess = 0;

    #endregion

    #region 请求参数问题（厂家 REQUEST_ERR_*）

    /// <summary>厂家 <c>REQUEST_ERR_NULL_ITEM</c>：请求体缺 <c>items</c>。</summary>
    public const int CodeRequestItemsMissing = 1;

    /// <summary>厂家 <c>REQUEST_ERR_OPERATION</c>：<c>operation</c> 不是 7 个合法值之一。</summary>
    public const int CodeRequestBadOperation = 2;

    /// <summary>厂家 <c>REQUEST_ERR_PATH</c>：<c>path</c> 缺失，或解析不到。</summary>
    public const int CodeRequestBadPath = 3;

    /// <summary>
    /// 厂家 <c>REQUEST_ERR_INDEX</c>：<c>index</c> 形状不被接受
    /// （例如传区间字符串 <c>"0-7"</c>、或数组里含非整数）。
    ///
    /// 曾把这个码当成"设备调用失败"的通用失败码——那是错的，
    /// 它是<b>参数</b>问题；设备执行失败用 <see cref="CodeExecuteFailed"/>。
    /// </summary>
    public const int CodeRequestBadIndex = 4;

    /// <summary>厂家 <c>REQUEST_ERR_KEY</c>：<c>key</c> 为空串。</summary>
    public const int CodeRequestBadKey = 5;

    /// <summary>
    /// 文件下载的 <c>length</c>/<c>offset</c> 越界。
    ///
    /// 厂家复用 <c>REQUEST_ERR_NULL_ITEM</c>(1)——语义上并不贴切（那不是"缺 items"），
    /// 但对外必须一致，所以这里另起名字而不是照抄那个错名。
    /// </summary>
    public const int CodeRequestInvalidRange = CodeRequestItemsMissing;

    /// <summary>厂家 <c>REQUEST_ERR_TIMEOUT</c>。</summary>
    public const int CodeRequestTimeout = 6;

    /// <summary>厂家 <c>REQUEST_ERR_SUBSCRIBE</c>。</summary>
    public const int CodeRequestSubscribe = 7;

    /// <summary>厂家 <c>REQUEST_ERR_MODEL</c>：模型相关参数问题。</summary>
    public const int CodeRequestModel = 8;

    /// <summary>厂家 <c>REQUEST_ERR_OTHER</c>。</summary>
    public const int CodeRequestOther = 9;

    #endregion

    #region 设备侧问题（厂家 DEVICE_ERR_*）

    /// <summary>厂家 <c>DEVICE_ERR_CONNECTION</c>。</summary>
    public const int CodeDeviceConnection = 10;

    /// <summary>厂家 <c>DEVICE_ERR_OTHER</c>。</summary>
    public const int CodeDeviceOther = 11;

    #endregion

    #region 执行问题（厂家 EXECUTE_ERR_*）

    /// <summary>厂家 <c>EXECUTE_ERR_DEVICE</c>：设备不存在 / 设备拒绝（也可理解为"设备侧参数不合法"）。</summary>
    public const int CodeExecuteDevice = 12;

    /// <summary>
    /// 厂家 <c>EXECUTE_ERR_REQUEST</c>：执行请求失败。
    ///
    /// 文件接口取不到大小时返回它，<b>但它的含义比"文件大小错误"宽</b>——
    /// 实测的文案（<c>"get the file size error"</c>）只是该场景的措辞。
    /// </summary>
    public const int CodeExecuteRequest = 13;

    /// <summary>厂家 <c>EXECUTE_ERR_EXECUTE</c>：设备调用抛异常。我们此前错用了 <c>4</c>。</summary>
    public const int CodeExecuteFailed = 14;

    /// <summary>厂家 <c>EXECUTE_ERR_INTERRUPT</c>。</summary>
    public const int CodeExecuteInterrupted = 15;

    /// <summary>厂家 <c>EXECUTE_ERR_TIMEOUT</c>：执行超时。</summary>
    public const int CodeExecuteTimeout = 16;

    /// <summary>厂家 <c>EXECUTE_ERR_SUBID</c>：采样 <c>sub_id</c> 不存在。</summary>
    public const int CodeExecuteBadSubId = 17;

    /// <summary>厂家 <c>EXECUTE_ERR_TOPIC</c>。</summary>
    public const int CodeExecuteTopic = 18;

    /// <summary>
    /// 厂家 <c>EXECUTE_ERR_OTHER</c>。
    ///
    /// 实测两个来源（时延可区分）：<c>key</c> 传空串（2 ms 内，本地校验）、
    /// 未知 SN（约 3012 ms，设备探测失败）。厂家把它归为"执行的其他错误"。
    /// </summary>
    public const int CodeExecuteOther = 19;

    /// <summary>厂家 <c>EXECUTE_ERR_ITEMS</c>。</summary>
    public const int CodeExecuteItems = 20;

    /// <summary>厂家 <c>FILE_IO_ERR</c>：文件读写失败。</summary>
    public const int CodeFileIo = 21;

    #endregion

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
    /// 不把内部细节泄漏给调用方。
    /// </summary>
    public static DataResponse FailFile(bool v2)
        => new()
        {
            Status = StatusFailed,
            Code = CodeExecuteRequest,
            Value = v2 ? "the file size error" : "get the file size error"
        };
}

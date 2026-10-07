using System.Text.Json;
using System.Text.Json.Serialization;

namespace NcLink.ApiServer.Models;

/// <summary>
/// 数据接口请求体，形状与官方 apiServer 的 <c>DataRequest</c> 一致
/// （取自 apiServer 自带 Swagger 与 <c>javap</c> 反查的字段）：
/// <code>
/// { "operation": "get_value", "items": [ ... ], "timeout": 8000 }
/// </code>
/// </summary>
public sealed class DataRequest
{
    /// <summary>
    /// 操作名。取值与官方手册表 5-1 一致，共 7 个：
    /// <c>get_value</c> / <c>set_value</c> / <c>get_length</c> / <c>get_keys</c> /
    /// <c>get_attributes</c> / <c>add</c> / <c>delete</c>。
    /// </summary>
    [JsonPropertyName("operation")]
    public string? Operation { get; set; }

    [JsonPropertyName("items")]
    public List<DataRequestItem>? Items { get; set; }

    /// <summary>超时（毫秒）。0 表示用服务端默认。</summary>
    [JsonPropertyName("timeout")]
    public long Timeout { get; set; }
}

/// <summary>
/// 数据项。字段与官方 <c>DataRequest$Item</c> 逐一对应。
/// </summary>
public sealed class DataRequestItem
{
    /// <summary>数据项路径，例如 <c>/MACHINE/CONTROLLER/VARIABLE@REG_X</c>。</summary>
    [JsonPropertyName("path")]
    public string? Path { get; set; }

    /// <summary>
    /// 索引。官方是 <c>Object</c>，实测接受两种形状：
    /// <list type="bullet">
    /// <item>单个整数 <c>0</c> → 返回 1 个值</item>
    /// <item>整数数组 <c>[0,1,2,3]</c> → 返回 4 个值</item>
    /// </list>
    /// 区间字符串 <c>"0-7"</c> 会被拒绝（<c>code=4</c>）。
    /// </summary>
    [JsonPropertyName("index")]
    public JsonElement? Index { get; set; }

    /// <summary>键。HASH 型数据项用它定位（官方同为 <c>Object</c>）。</summary>
    [JsonPropertyName("key")]
    public JsonElement? Key { get; set; }

    /// <summary>
    /// 长度。<b>对数据项不生效</b>——实测传 <c>offset</c>+<c>length</c> 返回 <c>[null]</c>。
    /// 它用于文件接口。
    /// </summary>
    [JsonPropertyName("length")]
    public int? Length { get; set; }

    /// <summary>位偏移 / 文件偏移。对数据项只有在 <c>set_value</c> 时表示位偏移。</summary>
    [JsonPropertyName("offset")]
    public int? Offset { get; set; }

    /// <summary>要写入的值（<c>set_value</c> 时使用）。</summary>
    [JsonPropertyName("value")]
    public JsonElement? Value { get; set; }
}

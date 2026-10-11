using System.Text.Json.Serialization;
using NcLink.Client.Devices.Engine;

namespace NcLink.Protocol.Models;
#region 注册

/// <summary>
/// 设备注册请求，主题 <c>Register/Request</c>（无设备后缀）。
///
/// 注意：本报文的关联字段是<b>小写</b> <c>deviceid</c>（无驼峰），与其它报文的 <c>@id</c> 不同。
/// 取值来自官方 <c>RegisterRequest</c> 的序列化注解。
/// </summary>
public sealed class RegisterRequestMessage : NcLinkEnvelope
{
    [JsonPropertyName("deviceid")]
    public string? DeviceId { get; set; }
}
#endregion
#region 探测

/// <summary>探测请求，主题 <c>Probe/Query/Request/{deviceId}</c>。报文体为空对象。</summary>
public sealed class ProbeQueryRequestMessage : NcLinkEnvelope
{
}

/// <summary>
/// 探测响应，主题 <c>Probe/Query/Response/{deviceId}</c>。
///
/// 格式经官方模拟器实测确认：
/// <code>
/// { "code": "ok", "probe": { "id": "01", "type": "NC_LINK_ROOT", "devices": [ ... ] } }
/// </code>
/// 注意两点与其它报文不同：
/// <list type="bullet">
/// <item><b>模型嵌在 <c>probe</c> 字段里</b>，不是顶层 <c>model</c>；</item>
/// <item><b>响应不回显 <c>@id</c></b>，因此探测响应只能靠主题归属 + <c>probe</c> 结构识别；</item>
/// <item><c>code</c> 是小写 <c>"ok"</c>（Query/Set 响应里是大写 <c>"OK"</c>）。</item>
/// </list>
/// 依据：<c>docs/official-simulator-verified-protocol.md</c>。
/// </summary>
public sealed class ProbeQueryResponseMessage : NcLinkEnvelope
{
    /// <summary>设备返回码，实测为小写 <c>ok</c>。</summary>
    [JsonPropertyName("code")]
    public string? Code { get; set; }

    /// <summary>
    /// 设备模型（与 <c>model.json</c> 同构）。
    ///
    /// 用 <see cref="System.Text.Json.JsonElement"/> 承接，因为它是一个嵌套对象、
    /// 且本层不需要理解其内部结构——解析由 <c>DeviceModelParser</c> 负责。
    /// </summary>
    [JsonPropertyName("probe")]
    public System.Text.Json.JsonElement Probe { get; set; }
}
#endregion
#region 查询

/// <summary>查询请求项。</summary>
public sealed class QueryRequestItemMessage
{
    /// <summary>数据项在模型中的 id。</summary>
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("params")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public QueryParamsMessage? Params { get; set; }
}

/// <summary>查询参数。索引以字符串数组表达区间，如 <c>["0-9","13"]</c>。</summary>
public sealed class QueryParamsMessage
{
    [JsonPropertyName("indexes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Indexes { get; set; }

    [JsonPropertyName("keys")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Keys { get; set; }

    [JsonPropertyName("offset")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? Offset { get; set; }

    [JsonPropertyName("length")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? Length { get; set; }

    [JsonPropertyName("operation")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Operation { get; set; }
}

/// <summary>查询请求，主题 <c>Query/Request/{deviceId}</c>。</summary>
public sealed class QueryRequestMessage : NcLinkEnvelope
{
    [JsonPropertyName("ids")]
    public List<QueryRequestItemMessage>? Ids { get; set; }
}

/// <summary>查询响应，主题 <c>Query/Response/{deviceId}</c>。</summary>
public sealed class QueryResponseMessage : NcLinkEnvelope
{
    [JsonPropertyName("values")]
    public List<QueryResponseItemMessage>? Values { get; set; }
}

/// <summary>查询响应项。</summary>
public sealed class QueryResponseItemMessage
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("code")]
    public string? Code { get; set; }

    [JsonPropertyName("reason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Reason { get; set; }

    [JsonPropertyName("params")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public QueryParamsMessage? Params { get; set; }

    /// <summary>值序列。元素类型不固定，保持为 <see cref="System.Text.Json.JsonElement"/>。</summary>
    [JsonPropertyName("values")]
    public List<System.Text.Json.JsonElement>? Values { get; set; }

    [JsonIgnore]
    public bool IsOk => NcLinkCodeNames.Parse(Code) == NcLinkCode.Ok;
}
#endregion
#region 设置

/// <summary>设置请求项。</summary>
public sealed class SetRequestItemMessage
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("params")]
    public SetParamsMessage? Params { get; set; }
}

/// <summary>设置参数。<c>offset</c> 存在时表示设置指定位。</summary>
public sealed class SetParamsMessage
{
    [JsonPropertyName("index")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? Index { get; set; }

    [JsonPropertyName("offset")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Offset { get; set; }

    /// <summary>
    /// 本块长度（厂家 <c>SetParams.length</c>）。
    ///
    /// 文件下发时每块都带它，且厂家要求响应回显 <c>offset</c> 与 <c>length</c>
    /// 才认这一块；缺了它，<c>value</c> 的十六进制串长度就无从校验。
    /// </summary>
    [JsonPropertyName("length")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? Length { get; set; }

    [JsonPropertyName("value")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public System.Text.Json.JsonElement? Value { get; set; }

    [JsonPropertyName("key")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Key { get; set; }

    [JsonPropertyName("operation")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Operation { get; set; }
}

/// <summary>设置请求，主题 <c>Set/Request/{deviceId}</c>。</summary>
public sealed class SetRequestMessage : NcLinkEnvelope
{
    [JsonPropertyName("values")]
    public List<SetRequestItemMessage>? Values { get; set; }
}

/// <summary>设置响应，主题 <c>Set/Response/{deviceId}</c>。</summary>
public sealed class SetResponseMessage : NcLinkEnvelope
{
    [JsonPropertyName("results")]
    public List<SetResponseItemMessage>? Results { get; set; }
}

/// <summary>设置响应项。</summary>
public sealed class SetResponseItemMessage
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("code")]
    public string? Code { get; set; }

    [JsonPropertyName("reason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Reason { get; set; }

    [JsonPropertyName("params")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SetParamsMessage? Params { get; set; }

    [JsonIgnore]
    public bool IsOk => NcLinkCodeNames.Parse(Code) == NcLinkCode.Ok;
}
#endregion
#region 采样

/// <summary>采样数据，主题 <c>Sample/{channelId}</c>。</summary>
public sealed class SampleMessage : NcLinkEnvelope
{
    /// <summary>采样通道 id。</summary>
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    /// <summary>设备给出的采样起始时间戳。</summary>
    [JsonPropertyName("beginTime")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? BeginTime { get; set; }

    [JsonPropertyName("data")]
    public List<SampleItemMessage>? Data { get; set; }
}

/// <summary>采样数据项。</summary>
public sealed class SampleItemMessage
{
    /// <summary>数据项在模型中的 id。</summary>
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    /// <summary>编码方式；为空表示原始值。</summary>
    [JsonPropertyName("encoding")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Encoding { get; set; }

    [JsonPropertyName("data")]
    public List<System.Text.Json.JsonElement>? Data { get; set; }
#endregion

}

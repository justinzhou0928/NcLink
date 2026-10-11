using System.Text.Json.Serialization;

namespace NcLink.Protocol;

/// <summary>
/// 所有 NC-Link 报文的基类。协议用 GUID 关联请求与响应，字段名为 <c>@id</c>。
/// </summary>
public abstract class NcLinkEnvelope
{
    /// <summary>
    /// 报文关联 id（协议字段 <c>@id</c>）。
    /// 注意：注册报文（Register/Request）不使用该字段，而是用 <c>deviceid</c>。
    /// </summary>
    [JsonPropertyName("@id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CorrelationId { get; set; }
}

/// <summary>
/// 收到的原始报文。传输层只负责字节与主题，解释交给编解码器。
/// </summary>
public sealed class RawMessage
{
    public RawMessage(string topic, byte[] payload)
    {
        Topic = topic ?? throw new ArgumentNullException(nameof(topic));
        Payload = payload ?? throw new ArgumentNullException(nameof(payload));
    }

    public string Topic { get; }

    public byte[] Payload { get; }

    /// <summary>按 UTF-8 解码的文本（明文 JSON 协议下有意义的便捷属性）。</summary>
    public string AsUtf8Text() => System.Text.Encoding.UTF8.GetString(Payload);
}

/// <summary>连接状态。</summary>
public enum NcLinkConnectionState
{
    Disconnected,
    Connecting,
    Connected
}

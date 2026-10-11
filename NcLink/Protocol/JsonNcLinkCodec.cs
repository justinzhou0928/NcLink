using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NcLink.Protocol;

namespace NcLink.Protocol;

/// <summary>NC-Link 报文的 JSON 序列化设置。</summary>
public static class NcLinkJson
{
    /// <summary>
    /// 与官方 Jackson 配置对齐：
    /// 忽略 null 字段（NON_NULL）、属性名不区分大小写、允许从字符串读数字。
    /// </summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static string Serialize(object value, Type type) => JsonSerializer.Serialize(value, type, Options);

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);

    public static object? Deserialize(string json, Type type) => JsonSerializer.Deserialize(json, type, Options);

    /// <summary>
    /// 直接序列化成 UTF-8 字节。
    ///
    /// 报文收发是热路径。<c>Serialize</c> 之后再 <c>Encoding.UTF8.GetBytes</c> 会先把整条报文
    /// 变成 UTF-16 字符串再转回来，白分配一份；<c>System.Text.Json</c> 本就支持直接写字节。
    /// </summary>
    public static byte[] SerializeToUtf8Bytes(object value, Type type)
        => JsonSerializer.SerializeToUtf8Bytes(value, type, Options);

    /// <summary>直接从 UTF-8 字节反序列化（同上，省掉一次字符串中转）。</summary>
    public static T? Deserialize<T>(ReadOnlySpan<byte> utf8Json)
        => JsonSerializer.Deserialize<T>(utf8Json, Options);

    /// <summary>直接从 UTF-8 字节反序列化到指定类型。</summary>
    public static object? Deserialize(ReadOnlySpan<byte> utf8Json, Type type)
        => JsonSerializer.Deserialize(utf8Json, type, Options);
}

/// <summary>
/// 明文 JSON 编解码器（默认实现）。
///
/// 依据：《HNC8-NOM 接口参考手册》中的报文示例均为明文 JSON；
/// 官方核心库另有 <c>Encoder</c>/<c>Decoder</c> 抽象，若现场真机使用其它编码，
/// 实现 <see cref="INcLinkCodec"/> 替换即可。
/// </summary>
public sealed class JsonNcLinkCodec : INcLinkCodec
{
    /// <summary>单例（无状态）。</summary>
    public static readonly JsonNcLinkCodec Default = new();

    public string Name => "json";

    public byte[] Encode(NcLinkEnvelope message)
    {
        ArgumentNullException.ThrowIfNull(message);

        // 必须按运行时类型序列化：若按 NcLinkEnvelope 基类序列化会丢掉派生报文的字段。
        // System.Text.Json 会缓存类型的元数据，重复调用同一类型不会有明显反射开销。
        return NcLinkJson.SerializeToUtf8Bytes(message, message.GetType());
    }

    public TMessage? Decode<TMessage>(byte[] payload) where TMessage : NcLinkEnvelope
    {
        if (payload.Length == 0) return null;
        try
        {
            return NcLinkJson.Deserialize<TMessage>(payload);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public NcLinkEnvelope? Decode(byte[] payload, Type messageType)
    {
        ArgumentNullException.ThrowIfNull(messageType);
        if (payload.Length == 0) return null;
        try
        {
            return NcLinkJson.Deserialize(payload, messageType) as NcLinkEnvelope;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

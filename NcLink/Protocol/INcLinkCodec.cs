namespace NcLink.Protocol;

/// <summary>
/// 报文编解码器。把报文对象与线上的字节互相转换。
///
/// 之所以把它独立成接口：官方 Java 核心库同时存在 <c>Encoder</c>/<c>Decoder</c>
/// 接口与 <c>DefaultEncoder</c>/<c>DefaultDecoder</c> 实现，而设备手册中的示例是明文 JSON。
/// 若现场真机使用其它编码方式，替换实现即可，上层无需改动。
/// </summary>
public interface INcLinkCodec
{
    /// <summary>实现名称，便于日志与诊断。</summary>
    string Name { get; }

    /// <summary>报文 → 线上字节。</summary>
    byte[] Encode(NcLinkEnvelope message);

    /// <summary>线上字节 → 报文；解析失败返回 null。</summary>
    TMessage? Decode<TMessage>(byte[] payload) where TMessage : NcLinkEnvelope;

    /// <summary>线上字节 → 报文（非泛型重载，供内部按类型解析使用）。</summary>
    NcLinkEnvelope? Decode(byte[] payload, Type messageType);
}

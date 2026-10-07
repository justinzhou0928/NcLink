namespace NcLink.ApiServer.Models;

/// <summary>
/// ApiServer 配置。默认值对齐官方 <c>application.yml</c>：
/// <code>
/// server.port: 19001
/// sample.cache.max: 100
/// sample.cache.timeout: 10000
/// sample.client.timeout: 600000
/// </code>
/// </summary>
public sealed class ApiServerOptions
{
    /// <summary>配置节名。</summary>
    public const string SectionName = "ApiServer";

    /// <summary>监听端口。默认 19001，与官方一致，便于直接替换。</summary>
    public int Port { get; set; } = 19001;

    /// <summary>MQTT Broker 地址。</summary>
    public string BrokerAddress { get; set; } = "tcp://127.0.0.1:1883";

    /// <summary>
    /// 每个采样订阅最多缓存多少帧（官方 <c>sample.cache.max</c>）。
    /// 超出后丢弃最旧的帧。
    /// </summary>
    public int SampleCacheMaxFrames { get; set; } = 100;

    /// <summary>
    /// 采样缓存条目空闲多久后回收（官方 <c>sample.cache.timeout</c>，毫秒）。
    /// </summary>
    public int SampleCacheIdleTimeoutMs { get; set; } = 10_000;

    /// <summary>
    /// 数据接口默认超时（官方 <c>sample.client.timeout</c>，毫秒）。
    /// 请求体未给 <c>timeout</c> 或给 0 时使用。
    /// </summary>
    public int DefaultRequestTimeoutMs { get; set; } = 10_000;

    /// <summary>G 代码默认编码（官方为 GB2312）。</summary>
    public string FileEncoding { get; set; } = "GB2312";
}

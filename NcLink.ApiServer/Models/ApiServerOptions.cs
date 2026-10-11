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

    /// <summary>
    /// 复刻官方 v2 的<b>缺陷</b>（默认 <c>false</c>）。
    ///
    /// 官方 0.3.10 的 v2 是系统性坏掉的——<c>SyncClient2</c> 把请求发到
    /// <c>Query/Request/{SN}/{clientId}</c>（四层），而设备只订阅三层
    /// <c>Query/Request/{SN}</c>，于是永远等不到响应；叠加 <c>execute2</c> 只等
    /// 1000 ms（v1 是 4000 ms），表现为恒定 <c>code 16</c>。
    ///
    /// <b>默认关闭：我们让 v2 真正工作。</b>打开这个开关则逐字节复刻官方行为，
    /// 用途是向厂家证明"我们完整理解你的实现"，而不是日常运行。
    /// </summary>
    public bool ReproduceOfficialV2Defects { get; set; }

    /// <summary>
    /// 是否开放 Swagger UI（默认 <c>true</c>）。
    ///
    /// <b>默认对齐官方</b>：官方 apiServer 无条件暴露 <c>/swagger-ui.html</c>，
    /// 关掉会破坏"可直接替换"的前提。
    ///
    /// 但 Microsoft 的官方文档把"只暴露 OpenAPI 文档、不暴露交互式 UI"列为
    /// 安全最佳实践（少披露信息），因此生产部署建议设为 <c>false</c>：
    /// <c>ApiServer:SwaggerUiEnabled=false</c>。
    /// 关掉只影响 <c>/swagger</c> 这个 UI，<b>不影响 <c>/openapi/v1.json</c></b>
    /// ——文档端点仍然可用，只是没有交互页面。
    /// </summary>
    public bool SwaggerUiEnabled { get; set; } = true;
}

namespace NcLink.Client.Connection;

/// <summary>
/// 客户端配置：**协议层与连接层**的通用设置。
///
/// 刻意<b>不含</b> Broker 地址、凭据、ClientId、重连等传输专有字段——
/// 那些属于具体传输实现（见 <c>NcLink.Transport.Mqtt</c> 的 <c>MqttTransportOptions</c>）。
/// 本包不引用任何 MQTT 库，若在这里声明 MQTT 字段，使用者会以为设了就生效。
///
/// 请求-响应关联的语义设置（超时、活动窗口）见 <see cref="CorrelationOptions"/>。
/// </summary>
public sealed class NcLinkClientOptions
{
    /// <summary>配置文件中的默认节名。</summary>
    public const string SectionName = "NcLink";

    /// <summary>
    /// 默认请求超时。构造时读入并交给 <c>RequestCorrelator</c>——
    /// <b>超时只在这一处配置</b>，关联语义选项（<see cref="CorrelationOptions"/>）不再重复声明。
    /// 单次调用可用 <c>timeout</c> 参数覆盖。
    /// </summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>验证配置合法性，返回全部问题描述（空数组表示合法）。</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();

        if (RequestTimeout <= TimeSpan.Zero)
            problems.Add("RequestTimeout 必须为正。");

        return problems;
    }
}

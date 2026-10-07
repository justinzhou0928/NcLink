using MQTTnet;
using MQTTnet.Protocol;

namespace NcLink.Transport.Mqtt;

/// <summary>
/// MQTT 传输配置（用 <c>services.AddNcLinkMqtt(...)</c> 或绑定配置节注册）。
///
/// 与核心库的 <c>NcLinkClientOptions</c> 分工：
/// 这里只放 <b>MQTT 专有</b>设置（地址、凭据、重连策略），
/// 请求超时、关联语义等协议层设置属核心库，不放这里。
/// </summary>
public sealed class MqttTransportOptions
{
    /// <summary>配置节名，供 <c>AddNcLinkMqtt(configuration.GetSection(...))</c> 使用。</summary>
    public const string SectionName = "NcLink:Mqtt";

    /// <summary>Broker 地址，形如 <c>tcp://127.0.0.1:1883</c>。</summary>
    public string BrokerAddress { get; set; } = "tcp://127.0.0.1:1883";

    /// <summary>用户名（Broker 未启用认证时留空）。</summary>
    public string? UserName { get; set; }

    /// <summary>密码。</summary>
    public string? Password { get; set; }

    /// <summary>客户端 id；留空则自动生成。同一 Broker 上必须唯一。</summary>
    public string? ClientId { get; set; }

    /// <summary>是否使用干净会话。</summary>
    public bool CleanSession { get; set; } = true;

    /// <summary>连接超时。</summary>
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>心跳周期。</summary>
    public TimeSpan KeepAlivePeriod { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>是否在连接丢失后自动重连。</summary>
    public bool AutoReconnect { get; set; } = true;

    /// <summary>重连起始退避时间。</summary>
    public TimeSpan ReconnectInitialDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>重连退避上限。</summary>
    public TimeSpan ReconnectMaxDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 订阅使用的 QoS，默认 <see cref="MqttQualityOfServiceLevel.AtLeastOnce"/>（1）。
    ///
    /// <b>默认值与官方 apiServer 一致</b>（实测它对设备订阅用 QoS 1）。
    /// QoS 0（AtMostOnce）在 broker 侧不重传，报文可能静默丢失——
    /// 表现为请求超时而不是拿到错数据，但会平白增加超时率。
    ///
    /// 用 QoS 1 后响应可能重复投递（至少一次语义）。关联器能正确处理：
    /// 带 <c>@id</c> 的重复响应认不到在途请求，会被丢弃并记日志。
    /// </summary>
    public MqttQualityOfServiceLevel SubscriptionQos { get; set; } = MqttQualityOfServiceLevel.AtLeastOnce;

    /// <summary>
    /// 发布请求使用的 QoS，默认 <see cref="MqttQualityOfServiceLevel.AtMostOnce"/>（0）。
    ///
    /// <b>刻意不跟订阅一起提到 QoS 1。</b>QoS 1 是"至少一次"，请求可能被重传，
    /// 而 <c>add</c> / <c>delete</c> <b>不是幂等操作</b>——重复下发会在设备上执行两次。
    /// <c>get_*</c> 与 <c>set_value</c> 重传无害，但传输层无法按操作区分取舍，
    /// 因此默认保持 0：宁可超时让调用方决定是否重试，也不冒重复写的风险。
    /// 确实需要更强投递保证时再打开，并自行确保操作的幂等性。
    /// </summary>
    public MqttQualityOfServiceLevel PublishQos { get; set; } = MqttQualityOfServiceLevel.AtMostOnce;

    /// <summary>验证配置合法性，返回全部问题描述（空数组表示合法）。</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(BrokerAddress))
        {
            problems.Add("BrokerAddress 不能为空。");
        }
        else
        {
            var (host, port) = MqttTransport.ParseEndpoint(BrokerAddress);
            if (string.IsNullOrWhiteSpace(host))
                problems.Add($"BrokerAddress 缺少主机名：{BrokerAddress}（应形如 tcp://127.0.0.1:1883）。");
            if (port is <= 0 or > 65535)
                problems.Add($"BrokerAddress 端口越界：{port}（应在 1..65535）。");
        }

        if (ConnectTimeout <= TimeSpan.Zero)
            problems.Add("ConnectTimeout 必须为正。");

        if (KeepAlivePeriod <= TimeSpan.Zero)
            problems.Add("KeepAlivePeriod 必须为正。");

        if (ReconnectInitialDelay <= TimeSpan.Zero)
            problems.Add("ReconnectInitialDelay 必须为正。");

        if (ReconnectMaxDelay < ReconnectInitialDelay)
            problems.Add("ReconnectMaxDelay 不能小于 ReconnectInitialDelay。");

        return problems;
    }
}

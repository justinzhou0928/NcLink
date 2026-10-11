using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using MQTTnet;
using NcLink.Protocol.Interfaces;

namespace NcLink.Transport.Mqtt;

/// <summary>
/// MQTT 传输的依赖注入扩展：<c>services.AddNcLinkMqtt(...)</c>。
///
/// 注册后 <see cref="INcLinkTransport"/> 由容器解析，
/// 调用方不必再写 <c>new MqttTransport(new MqttTransportOptions { ... })</c>：
/// <code>
/// services.AddLogging();
/// services.AddNcLinkMqtt(o =&gt; o.BrokerAddress = "tcp://127.0.0.1:1883");
/// services.AddNcLink();          // 不用再传 transportFactory
/// services.AddNcLinkHostedService();
/// </code>
///
/// 注册内容：
/// <list type="bullet">
/// <item><see cref="MqttTransportOptions"/> —— 单例，带启动时校验（<c>ValidateOnStart</c>）</item>
/// <item><c>Func&lt;IMqttClient&gt;</c> —— 单例，MQTTnet 默认客户端工厂（MQTTnet 5 无工厂接口，故用委托）</item>
/// <item><see cref="INcLinkTransport"/> —— 单例，<see cref="MqttTransport"/></item>
/// </list>
///
/// 一律单例：整个应用共享一条 MQTT 连接，且它承载全部请求-响应关联表。
/// </summary>
public static class MqttServiceCollectionExtensions
{
    /// <summary>用代码配置 MQTT 传输。</summary>
    public static IServiceCollection AddNcLinkMqtt(
        this IServiceCollection services,
        Action<MqttTransportOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var optionsBuilder = services.AddOptions<MqttTransportOptions>();
        if (configure is not null) optionsBuilder.Configure(configure);

        return services.AddNcLinkMqttCore(optionsBuilder);
    }

    /// <summary>从配置节绑定 MQTT 传输配置。</summary>
    public static IServiceCollection AddNcLinkMqtt(
        this IServiceCollection services,
        IConfiguration section)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(section);

        var optionsBuilder = services.AddOptions<MqttTransportOptions>().Bind(section);

        return services.AddNcLinkMqttCore(optionsBuilder);
    }

    private static IServiceCollection AddNcLinkMqttCore(
        this IServiceCollection services,
        OptionsBuilder<MqttTransportOptions> optionsBuilder)
    {
        optionsBuilder
            .Validate(o => o.Validate().Count == 0,
                "MqttTransportOptions 配置不合法（详见 Validate()）。")
            .ValidateOnStart();

        // MQTTnet 5 没有 IMqttClientFactory 接口，用工厂委托做注入点
        services.TryAddSingleton<Func<IMqttClient>>(
            _ => static () => new MqttClientFactory().CreateMqttClient());

        // 把配置解析成单例交给传输——构造函数参数一律必填，不用 IOptions 穿透各层
        services.TryAddSingleton(sp => sp.GetRequiredService<IOptions<MqttTransportOptions>>().Value);
        services.TryAddSingleton<INcLinkTransport, MqttTransport>();

        return services;
    }
}

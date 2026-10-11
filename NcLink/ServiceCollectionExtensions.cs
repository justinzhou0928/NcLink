using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NcLink.Client;
using NcLink.Client.Connection.Interfaces;
using NcLink.Client.Connection.Options;
using NcLink.Client.Connection.Engine;
using NcLink.Client.Devices.Interfaces;
using NcLink.Client.Devices.Engine;
using NcLink.Client.Files.Interfaces;
using NcLink.Client.Files.Engine;
using NcLink.Client.Query.Interfaces;
using NcLink.Client.Query.Engine;
using NcLink.Client.Set.Interfaces;
using NcLink.Client.Set.Engine;
using NcLink;
using NcLink.Protocol.Interfaces;
using NcLink.Protocol.Engine;

namespace NcLink;

/// <summary>
/// 依赖注入扩展：<c>services.AddNcLink(...)</c>。用法与注册内容见
/// <c>README.md</c> 的「依赖注入」一节——这里不重复维护示例。
///
/// 传输实现需另行注册：用 <c>NcLink.Transport.Mqtt</c> 的 <c>AddNcLinkMqtt(...)</c>，
/// 或注册自己的 <see cref="INcLinkTransport"/>。
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>用代码配置客户端。</summary>
    public static IServiceCollection AddNcLink(
        this IServiceCollection services,
        Action<NcLinkClientOptions>? configure = null,
        Func<IServiceProvider, INcLinkTransport>? transportFactory = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var optionsBuilder = services.AddOptions<NcLinkClientOptions>();
        if (configure is not null) optionsBuilder.Configure(configure);
        optionsBuilder
            .Validate(o => o.Validate().Count == 0,
                "NcLinkClientOptions 配置不合法（详见 Validate()）。")
            .ValidateOnStart();

        return services.AddNcLinkCore(transportFactory);
    }

    /// <summary>从配置节绑定客户端配置。</summary>
    public static IServiceCollection AddNcLink(
        this IServiceCollection services,
        IConfiguration section,
        Func<IServiceProvider, INcLinkTransport>? transportFactory = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(section);

        services.AddOptions<NcLinkClientOptions>()
            .Bind(section)
            .Validate(o => o.Validate().Count == 0,
                "NcLinkClientOptions 配置不合法（详见 Validate()）。")
            .ValidateOnStart();

        return services.AddNcLinkCore(transportFactory);
    }

    private static IServiceCollection AddNcLinkCore(
        this IServiceCollection services,
        Func<IServiceProvider, INcLinkTransport>? transportFactory)
    {
        if (transportFactory is not null)
            services.TryAddSingleton(transportFactory);

        services.TryAddSingleton<INcLinkCodec>(_ => JsonNcLinkCodec.Default);
        services.TryAddSingleton<INcLinkTopics>(_ => NcLinkTopics.Default);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(sp => sp.GetRequiredService<IOptions<CorrelationOptions>>().Value);

        services.TryAddSingleton<IDeviceProbe, DeviceProbe>();
        services.TryAddSingleton<DeviceModelCache>();
        services.TryAddSingleton<SampleMultiplexer>();
        services.TryAddSingleton<SampleSubscriptions>();
        services.TryAddSingleton<ISampling>(sp => sp.GetRequiredService<SampleSubscriptions>());

        services.TryAddSingleton(sp =>
        {
            var pipeline = new RequestPipeline(
                sp.GetRequiredService<INcLinkTransport>(),
                sp.GetRequiredService<INcLinkCodec>(),
                sp.GetRequiredService<INcLinkTopics>(),
                sp.GetRequiredService<IOptions<NcLinkClientOptions>>().Value,
                sp.GetRequiredService<CorrelationOptions>(),
                sp.GetRequiredService<ILoggerFactory>(),
                sp.GetRequiredService<TimeProvider>());

            // 采样转发接线放这里而非门面构造函数，且延迟解析 SampleMultiplexer
            // （它的依赖链会回到 IRequestPipeline）。理由见 AGENTS.md §2.5。
            pipeline.ForwardSamplesTo(message =>
                sp.GetRequiredService<SampleMultiplexer>().TryDispatch(message));

            return pipeline;
        });

        services.TryAddSingleton<IRequestPipeline>(sp => sp.GetRequiredService<RequestPipeline>());

        services.TryAddSingleton<DataItemReader>();
        services.TryAddSingleton<DataItemWriter>();
        services.TryAddSingleton<IDataItemReader>(sp => sp.GetRequiredService<DataItemReader>());
        services.TryAddSingleton<IDataItemWriter>(sp => sp.GetRequiredService<DataItemWriter>());

        services.TryAddSingleton<FileTransfer>();

        services.TryAddSingleton<IQuery>(sp => sp.GetRequiredService<DataItemReader>());
        services.TryAddSingleton<ISet>(sp => sp.GetRequiredService<DataItemWriter>());
        services.TryAddSingleton<IFileTransfer>(sp => sp.GetRequiredService<FileTransfer>());

        // 门面的构造函数是 internal（形参含 internal 类型），只能显式构造
        services.TryAddSingleton(sp => new NcLinkClient(
            sp.GetRequiredService<INcLinkTransport>(),
            sp.GetRequiredService<RequestPipeline>(),
            sp.GetRequiredService<ISampling>(),
            sp.GetRequiredService<IQuery>(),
            sp.GetRequiredService<ISet>(),
            sp.GetRequiredService<IFileTransfer>()));

        services.TryAddSingleton<INcLinkClient>(sp => sp.GetRequiredService<NcLinkClient>());

        return services;
    }

    /// <summary>
    /// 让客户端随 .NET 宿主启动而连接、随宿主停止而释放。
    ///
    /// 连接失败不会阻断宿主启动（由传输层的重连循环继续尝试），
    /// 因此 EMQX 晚启动或网络抖动不会导致整个应用起不来。
    /// </summary>
    public static IServiceCollection AddNcLinkHostedService(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddHostedService<NcLinkHostedService>();
        return services;
    }

    /// <summary>注册关联器的可选配置。</summary>
    public static IServiceCollection AddNcLinkCorrelation(
        this IServiceCollection services,
        Action<CorrelationOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.AddOptions<CorrelationOptions>().Configure(configure);
        return services;
    }
}

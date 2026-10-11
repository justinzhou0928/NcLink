using Microsoft.Extensions.DependencyInjection;
using NcLink;
using NcLink.Protocol.Interfaces;
using NcLink.Protocol.Engine;
using NcLink.Tests.Fakes;
using NcLink.Client;
using NcLink.Client.Connection.Options;
using NcLink.Extensions;

namespace NcLink.Tests.Extensions;

/// <summary>
/// DI 注册测试：确认 <c>services.AddNcLink(...)</c> 后容器能真正解析出可用的客户端。
///
/// 用 <see cref="FakeTransport"/> 作传输，因此不需要 Broker。
/// </summary>
public sealed class ServiceCollectionExtensionsTests
{
    [Fact]
    public async Task AddNcLink_WithCodeConfiguration_ShouldResolveClient()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNcLink(
            o => o.RequestTimeout = TimeSpan.FromSeconds(5),
            _ => new FakeTransport());

        await using var provider = services.BuildServiceProvider();

        var client = provider.GetRequiredService<NcLinkClient>();
        Assert.NotNull(client);

        // 接口解析到同一个单例（共享关联表与连接）
        var operations = provider.GetRequiredService<INcLinkClient>();
        Assert.Same(client, operations);
    }

    [Fact]
    public async Task AddNcLink_ShouldRegisterTransportCodecAndTopicsAsSingletons()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNcLink(transportFactory: _ => new FakeTransport());

        await using var provider = services.BuildServiceProvider();

        Assert.Same(provider.GetRequiredService<INcLinkTransport>(),
            provider.GetRequiredService<INcLinkTransport>());
        Assert.IsType<JsonNcLinkCodec>(provider.GetRequiredService<INcLinkCodec>());
        Assert.Same(NcLinkTopics.Default, provider.GetRequiredService<INcLinkTopics>());
    }

    [Fact]
    public async Task AddNcLink_ShouldApplyCodeConfiguration()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNcLink(o => o.RequestTimeout = TimeSpan.FromSeconds(7), _ => new FakeTransport());

        await using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<
            Microsoft.Extensions.Options.IOptions<NcLinkClientOptions>>().Value;

        Assert.Equal(TimeSpan.FromSeconds(7), options.RequestTimeout);
    }

    [Fact]
    public async Task AddNcLink_InvalidOptions_ShouldFailValidationOnStart()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNcLink(o =>
        {
            o.RequestTimeout = TimeSpan.Zero;   // 非法：必须为正
        }, _ => new FakeTransport());

        await using var provider = services.BuildServiceProvider();

        // ValidateOnStart：首次解析 IOptions 时触发校验
        var exception = await Assert.ThrowsAnyAsync<Exception>(async () =>
            await Task.Run(() => provider.GetRequiredService<
                Microsoft.Extensions.Options.IOptions<NcLinkClientOptions>>().Value));

        Assert.Contains("配置不合法", exception.Message);
    }

    [Fact]
    public async Task AddNcLink_WithoutTransportFactory_ShouldRequireCallerRegistration()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNcLink();   // 不提供传输工厂

        await using var provider = services.BuildServiceProvider();

        // 未注册传输时解析客户端应明确报错，而不是静默返回坏对象
        Assert.ThrowsAny<InvalidOperationException>(() =>
            provider.GetRequiredService<NcLinkClient>());
    }

    [Fact]
    public async Task AddNcLinkCorrelation_ShouldApplyActivityWindow()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNcLink(transportFactory: _ => new FakeTransport());
        services.AddNcLinkCorrelation(o => o.ActivityWindow = TimeSpan.FromMinutes(17));

        await using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<
            Microsoft.Extensions.Options.IOptions<NcLink.Client.Connection.Options.CorrelationOptions>>().Value;

        Assert.Equal(TimeSpan.FromMinutes(17), options.ActivityWindow);
    }

    [Fact]
    public void CorrelationOptions_ShouldNotDuplicateRequestTimeout()
    {
        // 超时的唯一来源是 NcLinkClientOptions.RequestTimeout。
        // 若 CorrelationOptions 又出现一个超时属性，就会产生"改了一个不生效"的困惑。
        var propertyNames = typeof(NcLink.Client.Connection.Options.CorrelationOptions)
            .GetProperties().Select(p => p.Name).ToArray();

        Assert.DoesNotContain("DefaultTimeout", propertyNames);
        Assert.Contains("ActivityWindow", propertyNames);
    }
}

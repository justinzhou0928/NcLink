using Microsoft.Extensions.DependencyInjection;
using NcLink.Client;
using NcLink.Client.Devices.Engine;
using NcLink;
using NcLink.Protocol.Interfaces;
using NcLink.Tests.Fakes;
using NcLink.Transport.Mqtt;
using NcLink.Client.Files.Interfaces;
using NcLink.Extensions;

namespace NcLink.Tests.Extensions;

/// <summary>
/// 容器装配与释放的行为测试：<b>不需要 MQTT Broker</b>。
///
/// 重点是<b>释放路径</b>：只实现 <see cref="IAsyncDisposable"/> 的单例会让
/// <c>ServiceProvider.Dispose()</c> 抛异常，而
/// <c>using var provider = services.BuildServiceProvider()</c> 是常见写法。
/// </summary>
public sealed class ContainerLifetimeTests
{
    [Fact]
    public void SyncDispose_AfterResolvingClient_ShouldNotThrow()
    {
        // 回归测试：NcLinkClient 必须同时实现 IDisposable，
        // 否则容器同步释放会抛
        // "'NcLinkClient' type only implements IAsyncDisposable"
        var transport = new FakeTransport();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNcLink(transportFactory: _ => transport);

        using (var provider = services.BuildServiceProvider())
        {
            _ = provider.GetRequiredService<INcLinkClient>();
        }
    }

    [Fact]
    public void SyncDispose_WithoutResolvingAnything_ShouldNotThrow()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNcLink(transportFactory: _ => new FakeTransport());

        using var provider = services.BuildServiceProvider();

        // 注册了但从未解析：也不应因为"存在 IAsyncDisposable-only 单例"而报错
    }

    [Fact]
    public void SyncDispose_WithRealMqttTransportRegistered_ShouldNotThrow()
    {
        // MqttTransport 自身也要能同步释放（Dispose 转调 DisposeAsync）
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNcLinkMqtt(o => o.BrokerAddress = "tcp://127.0.0.1:1883");
        services.AddNcLink();

        using (var provider = services.BuildServiceProvider())
        {
            _ = provider.GetRequiredService<INcLinkTransport>();
        }
    }

    [Fact]
    public async Task AsyncDispose_ShouldAlsoWork()
    {
        var transport = new FakeTransport();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNcLink(transportFactory: _ => transport);

        await using (var provider = services.BuildServiceProvider())
        {
            _ = provider.GetRequiredService<INcLinkClient>();
        }

        Assert.False(transport.IsConnected);
    }

    [Fact]
    public async Task ClientDispose_ShouldBeIdempotent()
    {
        // 容器释放会调用 Dispose，测试自身的 await using 可能再调 DisposeAsync
        var transport = new FakeTransport();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNcLink(transportFactory: _ => transport);

        using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<NcLinkClient>();

        client.Dispose();
        client.Dispose();
        await client.DisposeAsync();
        await client.DisposeAsync();
    }

    [Fact]
    public void DisposedClient_ShouldThrowObjectDisposedOnUse()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNcLink(transportFactory: _ => new FakeTransport());

        using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<NcLinkClient>();

        client.Dispose();

        Assert.Throws<ObjectDisposedException>(() => client.RegisterModel(
            SampleDeviceModel.Parse("D")));
    }

    [Fact]
    public void Registrations_ShouldAllBeSingletons()
    {
        // 同一单例被重复解析必须返回同一实例——否则每个请求会各建一套关联表
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNcLink(transportFactory: _ => new FakeTransport());

        using var provider = services.BuildServiceProvider();

        // 门面：INcLinkClient 与 NcLinkClient 是同一实例
        var client = provider.GetRequiredService<NcLinkClient>();
        Assert.Same(client, provider.GetRequiredService<INcLinkClient>());
        Assert.Same(client, provider.GetRequiredService<NcLinkClient>());

        // 能力接口注册到各自的实现（不是门面）
        Assert.Same(
            provider.GetRequiredService<NcLink.Client.Query.Engine.DataItemReader>(),
            provider.GetRequiredService<NcLink.Client.Query.Interfaces.IQuery>());
        Assert.Same(
            provider.GetRequiredService<NcLink.Client.Set.Engine.DataItemWriter>(),
            provider.GetRequiredService<NcLink.Client.Set.Interfaces.ISet>());

        // 文件能力：门面的 Files 就是容器里的 IFileTransfer
        Assert.Same(client.Files, provider.GetRequiredService<NcLink.Client.Files.Interfaces.IFileTransfer>());

        // 传输是单例
        Assert.Same(
            provider.GetRequiredService<INcLinkTransport>(),
            provider.GetRequiredService<INcLinkTransport>());
    }
}

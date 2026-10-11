using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NcLink.Client;
using NcLink;
using NcLink.Protocol.Models;
using NcLink.Tests.Fakes;

namespace NcLink.Tests.Hosting;

/// <summary>
/// <see cref="NcLinkHostedService"/> 单元测试：<b>不需要 MQTT Broker</b>。
///
/// 客户端一律通过 <c>AddNcLink</c> 容器解析——
/// 与生产代码走同一条装配路径，因此这些测试同时验证注册是完整的。
/// </summary>
public sealed class NcLinkHostedServiceTests
{
    [Fact]
    public async Task StartAsync_ShouldConnectClient()
    {
        var transport = new FakeTransport();
        await using var provider = BuildProvider(transport);
        var client = provider.GetRequiredService<NcLinkClient>();
        var hosted = Create(provider);

        await hosted.StartAsync(CancellationToken.None);

        Assert.True(transport.IsConnected);
        Assert.Equal(NcLinkConnectionState.Connected, client.ConnectionState);
    }

    [Fact]
    public async Task StartAsync_WhenBrokerUnreachable_ShouldNotThrow()
    {
        // 连接失败不能阻断宿主启动，否则 EMQX 晚启动几秒整个应用就起不来
        var transport = new FakeTransport
        {
            OnConnect = _ => throw new InvalidOperationException("broker 不可达")
        };
        await using var provider = BuildProvider(transport);
        var hosted = Create(provider);

        // 不应抛出
        await hosted.StartAsync(CancellationToken.None);

        Assert.False(transport.IsConnected);
    }

    [Fact]
    public async Task StartAsync_ShouldBeIdempotent()
    {
        var transport = new FakeTransport();
        await using var provider = BuildProvider(transport);
        var hosted = Create(provider);

        await hosted.StartAsync(CancellationToken.None);
        await hosted.StartAsync(CancellationToken.None);

        // 客户端初始化是幂等的，不应重复连接
        Assert.Equal(1, transport.ConnectCount);
    }

    [Fact]
    public async Task StartAsync_WithCancellation_ShouldPropagateCancellation()
    {
        // 取消是调用方的意图，不应被"不阻断启动"的 catch 吞掉
        var transport = new FakeTransport
        {
            OnConnect = ct =>
            {
                ct.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            }
        };
        await using var provider = BuildProvider(transport);
        var hosted = Create(provider);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => hosted.StartAsync(cts.Token));
    }

    [Fact]
    public async Task StopAsync_ShouldReleaseTransport()
    {
        var transport = new FakeTransport();
        await using var provider = BuildProvider(transport);
        var hosted = Create(provider);

        await hosted.StartAsync(CancellationToken.None);
        await hosted.StopAsync(CancellationToken.None);

        Assert.False(transport.IsConnected);
    }

    [Fact]
    public void Constructor_ShouldRejectNullClient()
    {
        Assert.Throws<ArgumentNullException>(() => new NcLinkHostedService(null!, null!));
    }

    /// <summary>宿主服务由容器解析（含必填的 <c>ILogger</c>）。</summary>
    private static NcLinkHostedService Create(ServiceProvider provider)
        => ActivatorUtilities.CreateInstance<NcLinkHostedService>(provider);

    private static ServiceProvider BuildProvider(FakeTransport transport)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNcLink(
            o => o.RequestTimeout = TimeSpan.FromMilliseconds(200),
            _ => transport);
        return services.BuildServiceProvider();
    }
}

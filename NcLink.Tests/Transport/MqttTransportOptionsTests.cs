using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NcLink;
using NcLink.Extensions;
using NcLink.Protocol.Contracts;
using NcLink.Transport.Mqtt;

namespace NcLink.Tests.Transport;

/// <summary>
/// MQTT 传输配置与依赖注入注册的单元测试：<b>不需要 MQTT Broker</b>。
///
/// 覆盖三件事：地址解析、配置校验、<c>AddNcLinkMqtt</c> 的注册可用性。
/// </summary>
public sealed class MqttTransportOptionsTests
{
    [Theory]
    [InlineData("tcp://127.0.0.1:1883", "127.0.0.1", 1883)]
    [InlineData("tcp://broker.local:8883", "broker.local", 8883)]
    [InlineData("127.0.0.1:1883", "127.0.0.1", 1883)]          // 无 scheme
    [InlineData("tcp://127.0.0.1", "127.0.0.1", 1883)]          // 缺省端口
    [InlineData("mqtt://10.0.0.9:1884", "10.0.0.9", 1884)]      // 其它 scheme
    public void ParseEndpoint_ShouldSplitHostAndPort(string address, string host, int port)
    {
        var (h, p) = MqttTransport.ParseEndpoint(address);

        Assert.Equal(host, h);
        Assert.Equal(port, p);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ParseEndpoint_EmptyAddress_ShouldThrow(string address)
        => Assert.ThrowsAny<ArgumentException>(() => MqttTransport.ParseEndpoint(address));

    [Fact]
    public void DefaultOptions_ShouldBeValid()
        => Assert.Empty(new MqttTransportOptions().Validate());

    [Fact]
    public void Validate_ShouldAccumulateAllProblems()
    {
        var options = new MqttTransportOptions
        {
            BrokerAddress = "://",
            ConnectTimeout = TimeSpan.Zero,
            KeepAlivePeriod = TimeSpan.Zero,
            ReconnectInitialDelay = TimeSpan.FromSeconds(10),
            ReconnectMaxDelay = TimeSpan.FromSeconds(1)     // 小于起始退避
        };

        var problems = options.Validate();

        Assert.Contains(problems, p => p.Contains("BrokerAddress"));
        Assert.Contains(problems, p => p.Contains("ConnectTimeout"));
        Assert.Contains(problems, p => p.Contains("KeepAlivePeriod"));
        Assert.Contains(problems, p => p.Contains("ReconnectMaxDelay"));
    }

    [Theory]
    [InlineData("tcp://127.0.0.1:0")]
    [InlineData("tcp://127.0.0.1:70000")]
    public void Validate_PortOutOfRange_ShouldReport(string address)
    {
        var problems = new MqttTransportOptions { BrokerAddress = address }.Validate();

        Assert.Contains(problems, p => p.Contains("端口越界"));
    }

    [Fact]
    public async Task AddNcLinkMqtt_ShouldRegisterTransportResolvableFromContainer()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNcLinkMqtt(o => o.BrokerAddress = "tcp://10.0.0.9:1884");

        await using var provider = services.BuildServiceProvider();

        var transport = provider.GetRequiredService<INcLinkTransport>();

        Assert.IsType<MqttTransport>(transport);
        Assert.Equal("tcp://10.0.0.9:1884", transport.Endpoint);
    }

    [Fact]
    public async Task AddNcLinkMqtt_ShouldApplyCodeConfiguration()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNcLinkMqtt(o =>
        {
            o.BrokerAddress = "tcp://192.168.1.5:1885";
            o.ClientId = "my-client";
            o.UserName = "user";
        });

        await using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<MqttTransportOptions>>().Value;

        Assert.Equal("tcp://192.168.1.5:1885", options.BrokerAddress);
        Assert.Equal("my-client", options.ClientId);
        Assert.Equal("user", options.UserName);
    }

    [Fact]
    public async Task AddNcLinkMqtt_InvalidOptions_ShouldFailValidationOnStart()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNcLinkMqtt(o => o.ConnectTimeout = TimeSpan.Zero);

        await using var provider = services.BuildServiceProvider();

        // ValidateOnStart：首次解析 IOptions 时触发校验
        var exception = await Assert.ThrowsAnyAsync<Exception>(async () =>
            await Task.Run(() => provider.GetRequiredService<
                IOptions<MqttTransportOptions>>().Value));

        Assert.Contains("配置不合法", exception.Message);
    }

    [Fact]
    public async Task AddNcLinkMqtt_ShouldNotOverrideCallerRegisteredTransport()
    {
        // TryAdd 语义：调用方先注册自己的传输时，不应被覆盖
        var custom = new NcLink.Tests.Fakes.FakeTransport();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<INcLinkTransport>(custom);
        services.AddNcLinkMqtt();

        await using var provider = services.BuildServiceProvider();

        Assert.Same(custom, provider.GetRequiredService<INcLinkTransport>());
    }

    [Fact]
    public async Task AddNcLinkMqtt_ShouldPlugIntoAddNcLinkWithoutFactory()
    {
        // 核心价值：注册后 AddNcLink 不再需要 transportFactory 参数
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNcLinkMqtt(o => o.BrokerAddress = "tcp://127.0.0.1:1883");
        services.AddNcLink(o => o.RequestTimeout = TimeSpan.FromSeconds(5));

        await using var provider = services.BuildServiceProvider();

        Assert.IsType<MqttTransport>(provider.GetRequiredService<INcLinkTransport>());
        Assert.NotNull(provider.GetRequiredService<NcLink.Client.NcLinkClient>());
    }

    [Fact]
    public void Constructor_ShouldRejectNullArguments()
    {
        var options = new MqttTransportOptions();

        Assert.Throws<ArgumentNullException>(() => new MqttTransport(null!));
        Assert.Throws<ArgumentNullException>(() => new MqttTransport(options, (Func<MQTTnet.IMqttClient>)null!));
    }
}

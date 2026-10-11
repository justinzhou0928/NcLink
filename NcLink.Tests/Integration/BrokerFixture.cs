using System.Net.Sockets;
using NcLink.Simulator;
using NcLink.Transport.Mqtt;
using Microsoft.Extensions.Logging;
using NcLink.Client;
using NcLink.Client.Connection.Options;
using NcLink.Client.Devices.Models;
using Microsoft.Extensions.DependencyInjection;
using NcLink;

namespace NcLink.Tests.Integration;

/// <summary>
/// 集成测试基座：进程内启动模拟机床 + 真实 MQTT 客户端。
///
/// 需要本机有 MQTT Broker（默认 127.0.0.1:1883）。若端口不可达则跳过而非失败——
/// 这样在没有 Broker 的机器上 <c>dotnet test</c> 依然全绿。
/// </summary>
public sealed class BrokerFixture : IAsyncDisposable
{
    public const string DefaultHost = "127.0.0.1";
    public const int DefaultPort = 1883;

    private readonly List<SimulatedMachine> _machines = [];

    /// <summary>为本夹具创建的客户端容器，释放时统一收尾。</summary>
    private readonly List<ServiceProvider> _clientProviders = [];
    private readonly Dictionary<string, SimulatedMachine> _byDeviceId = new(StringComparer.Ordinal);

    /// <param name="devicePrefix">
    /// 设备 SN 前缀。各测试类必须使用不同前缀：诊断类会主动让设备掉线，
    /// 若与其它类共用 SN，掉线状态会跨测试泄漏。
    /// </param>
    /// <param name="deviceCount">
    /// 基座预启动的设备台数。传 0 表示不预启动，
    /// 由用例通过 <see cref="StartMachineAsync"/> 按需创建（推荐，避免用例间共享设备）。
    /// </param>
    public BrokerFixture(string devicePrefix = "IT", int deviceCount = 4)
    {
        DeviceIds = Enumerable.Range(1, deviceCount)
            .Select(i => $"{devicePrefix}_DEV_{i:D5}")
            .ToArray();

        PrimaryDeviceId = DeviceIds.Count > 0 ? DeviceIds[0] : null;
    }

    public bool BrokerAvailable { get; private set; }

    /// <summary>本基座预启动的设备 SN（按需创建的设备不在其中）。</summary>
    public IReadOnlyList<string> DeviceIds { get; }

    /// <summary>主设备 SN；基座未预启动设备时为 null。</summary>
    public string? PrimaryDeviceId { get; }

    /// <summary>取已启动设备的句柄（用于单独 <c>StopAsync</c> 模拟掉线）。</summary>
    public SimulatedMachine GetMachine(string deviceId)
        => _byDeviceId.TryGetValue(deviceId, out var machine)
            ? machine
            : throw new InvalidOperationException(
                $"基座未启动设备 {deviceId}。已启动：{string.Join(", ", _byDeviceId.Keys)}");

    /// <summary>启动本基座的全部设备。返回 Broker 是否可用。</summary>
    /// <param name="enableSampling">是否为设备启用采样通道（默认关闭，见 StartMachineAsync）。</param>
    public async Task<bool> StartAsync(bool enableSampling = false)
    {
        BrokerAvailable = await IsBrokerReachableAsync(DefaultHost, DefaultPort);
        if (!BrokerAvailable) return false;

        for (var i = 0; i < DeviceIds.Count; i++)
            await StartMachineAsync(DeviceIds[i], $"it_channel{i}", enableSampling);

        return true;
    }

    /// <summary>
    /// 按指定 SN 启动一台模拟设备，返回其句柄。
    /// </summary>
    /// <param name="deviceId">设备 SN。</param>
    /// <param name="channelId">采样通道 id。</param>
    /// <param name="enableSampling">
    /// 是否启用采样通道。<b>默认关闭</b>：
    /// 采样会持续产生 MQTT 流量，多台设备同时高频采样会干扰其它用例的请求-响应时序。
    /// 只有需要验证采样的用例才应打开。
    /// </param>
    /// <param name="sampleIntervalMs">采样周期，仅 <paramref name="enableSampling"/> 为 true 时有意义。</param>
    public async Task<SimulatedMachine> StartMachineAsync(
        string deviceId,
        string channelId,
        bool enableSampling = false,
        int sampleIntervalMs = 200)
    {
        var channels = new List<SimulatedSampleChannel>();
        if (enableSampling)
        {
            channels.Add(new SimulatedSampleChannel
            {
                ChannelId = channelId,
                SampleIntervalMs = sampleIntervalMs,
                ItemIds = ["010302", "010303", "01035412"]
            });
        }

        var machine = new SimulatedMachine(new SimulatedMachineOptions
        {
            DeviceId = deviceId,
            Host = DefaultHost,
            Port = DefaultPort,
            ModelJson = EmbeddedSampleModel.Json,
            SampleChannels = channels
        });

        await machine.StartAsync();
        _machines.Add(machine);
        _byDeviceId[deviceId] = machine;
        return machine;
    }

    /// <summary>
    /// 用<b>真实的依赖注入容器</b>构建一个连到测试 Broker 的客户端。
    ///
    /// 走容器而非手工 <c>new</c>：与生产装配路径一致，
    /// 因此这些集成测试同时验证 <c>AddNcLinkMqtt</c> + <c>AddNcLink</c>
    /// 在真实传输下也能解析。
    /// 返回的客户端已 <c>InitializeAsync</c> 完毕，可直接发请求。
    /// </summary>
    public NcLinkClient CreateClient(int timeoutMs = 3000, ILoggerFactory? loggerFactory = null)
    {
        var services = new ServiceCollection();
        if (loggerFactory is null)
        {
            services.AddLogging();
        }
        else
        {
            // 只注册 ILoggerFactory 不够：组件注入的是 ILogger<T>，
            // 必须把开放的 ILogger<> 也绑到同一个工厂，否则解析会失败。
            services.AddSingleton(loggerFactory);
            services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        }

        // Broker 地址等传输专有设置属于 MqttTransportOptions，不属于 NcLinkClientOptions
        services.AddNcLinkMqtt(o =>
        {
            o.BrokerAddress = $"tcp://{DefaultHost}:{DefaultPort}";
            o.ClientId = $"it-{Guid.NewGuid():N}"[..20];
        });

        // 传输已注册，AddNcLink 不再需要 transportFactory 参数
        services.AddNcLink(o => o.RequestTimeout = TimeSpan.FromMilliseconds(timeoutMs));

        var provider = services.BuildServiceProvider();
        _clientProviders.Add(provider);

        var client = provider.GetRequiredService<NcLinkClient>();
        client.InitializeAsync().GetAwaiter().GetResult();   // 测试自己的异步初始化在别处
        return client;
    }

    private static async Task<bool> IsBrokerReachableAsync(string host, int port)
    {
        try
        {
            using var client = new TcpClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await client.ConnectAsync(host, port, cts.Token);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var machine in _machines)
            await machine.DisposeAsync();

        // 容器持有客户端与传输，释放容器即完成收尾
        foreach (var provider in _clientProviders)
            await provider.DisposeAsync();
    }
}

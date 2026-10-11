using NcLink.Client;
using NcLink.Client.Query.Models;
using NcLink.Exceptions;

namespace NcLink.Tests.Integration;

/// <summary>
/// 端到端集成测试：真实 MQTT + 进程内模拟机床。
/// 覆盖「探测 → 查询 → 设置 → 位操作 → 采样 → 文件」全链路。
/// </summary>
public sealed class EndToEndTests : IAsyncLifetime
{
    // 启用采样：本类含采样订阅用例。周期放大到 200ms，
    // 够验证"能收到帧"，又不会让采样流量干扰其它请求的时序。
    private readonly BrokerFixture _fixture = new("E2E", deviceCount: 1);
    private bool _skip;

    /// <summary>准备：启动进程内模拟机床。若本机没有 Broker，则后续用例直接通过（跳过语义）。</summary>
    public async Task InitializeAsync() => _skip = !await _fixture.StartAsync(enableSampling: true);

    public async Task DisposeAsync() => await _fixture.DisposeAsync();

    private string DeviceId => _fixture.PrimaryDeviceId
        ?? throw new InvalidOperationException("本测试类要求基座预启动设备。");

    [Fact]
    public async Task Probe_ShouldReturnDeviceModel()
    {
        if (_skip) return;

        await using var client = _fixture.CreateClient();
        var model = await client.ProbeAsync(DeviceId);

        Assert.Equal(DeviceId, model.DeviceId);
        Assert.NotEmpty(model.Nodes);
        Assert.NotNull(model.FindId("/MACHINE/CONTROLLER/VARIABLE@REG_X"));
    }

    [Fact]
    public async Task QueryRegister_ShouldReturnRequestedRange()
    {
        if (_skip) return;

        await using var client = _fixture.CreateClient();
        var result = await client.QueryItemAsync(DeviceId,
            DataItemRequest.At("/MACHINE/CONTROLLER/VARIABLE@REG_X", 0, 9));

        Assert.True(result.Succeeded);
        Assert.Equal(10, result.Count);
    }

    [Fact]
    public async Task SetAndReadBack_ShouldRoundTrip()
    {
        if (_skip) return;

        await using var client = _fixture.CreateClient();

        var setOk = await client.SetItemAsync(DeviceId,
            DataItemRequest.Set("/MACHINE/CONTROLLER/VARIABLE@REG_X", 4242, index: 20));
        Assert.True(setOk);

        var readback = await client.QueryItemAsync(DeviceId,
            DataItemRequest.At("/MACHINE/CONTROLLER/VARIABLE@REG_X", 20));

        Assert.Equal(4242, readback.IntAt(0));
    }

    [Fact]
    public async Task SetBit_ShouldOnlyAffectTargetBit()
    {
        if (_skip) return;

        await using var client = _fixture.CreateClient();

        // 先把 X[30] 清零，再置第 5 位 → 期望 32
        await client.SetItemAsync(DeviceId,
            DataItemRequest.Set("/MACHINE/CONTROLLER/VARIABLE@REG_X", 0, index: 30));
        await client.SetItemAsync(DeviceId,
            DataItemRequest.Set("/MACHINE/CONTROLLER/VARIABLE@REG_X", 1, index: 30, bitOffset: 5));

        var readback = await client.QueryItemAsync(DeviceId,
            DataItemRequest.At("/MACHINE/CONTROLLER/VARIABLE@REG_X", 30));

        Assert.Equal(32, readback.IntAt(0));
    }

    [Fact]
    public async Task SampleSubscription_ShouldReceiveFrames()
    {
        if (_skip) return;

        await using var client = _fixture.CreateClient();
        var frames = new List<SampleFrame>();
        var gate = new object();
        var firstFrame = new TaskCompletionSource<SampleFrame>(TaskCreationOptions.RunContinuationsAsynchronously);

        var subscription = await client.SubscribeSampleAsync(DeviceId, "it_channel0");
        subscription.FrameReceived += (_, frame) =>
        {
            lock (gate) frames.Add(frame);
            firstFrame.TrySetResult(frame);
        };

        // 等信号 + 超时，避免固定 sleep 在高负载下偶发失败
        var received = await firstFrame.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("it_channel0", received.ChannelId);
        Assert.Equal(DeviceId, received.DeviceId);

        await subscription.UnsubscribeAsync();

        List<SampleFrame> snapshot;
        lock (gate) snapshot = frames.ToList();

        Assert.NotEmpty(snapshot);
        Assert.All(snapshot, frame => Assert.Equal("it_channel0", frame.ChannelId));
        Assert.All(snapshot, frame => Assert.Equal(DeviceId, frame.DeviceId));
    }

    [Fact]
    public async Task FileTransfer_ShouldRoundTripWithGb2312()
    {
        if (_skip) return;

        await using var client = _fixture.CreateClient();
        var files = client.Files;

        var names = await files.ListFilesAsync(DeviceId);
        Assert.NotEmpty(names);

        var source = names[0];

        // 诊断包装：全量运行时若失败，需要能立刻看出是哪一步、什么异常
        try
        {
            var entries0 = await files.GetFileEntriesAsync(DeviceId, [source]);
            Assert.NotEmpty(entries0);

            var size = entries0[0].Size;
            Assert.True(size > 0, $"文件 {source} 大小应为正，实际 {size}");

            var content = await files.DownloadTextAsync(DeviceId, source, size);
            Assert.Contains("M30", content);

            // 下发含中文的内容，验证 GB2312 往返
            const string target = "IT_PROG";
            const string body = "%100\n(中文注释)\nM30\n%\n";
            await files.UploadTextAsync(DeviceId, target, body);

            var readback = await files.DownloadTextAsync(DeviceId, target);
            Assert.Contains("中文注释", readback);
        }
        catch (Exception e) when (e is not Xunit.Sdk.XunitException)
        {
            Assert.Fail($"文件传输往返失败（设备 {DeviceId}，文件列表 [{string.Join(", ", names)}]，"
                        + $"取 {source}）：{e.GetType().Name}: {e.Message}");
        }
    }

    [Fact]
    public async Task QueryUnknownDevice_ShouldThrowUnavailable()
    {
        if (_skip) return;

        await using var client = _fixture.CreateClient(timeoutMs: 1200);

        await Assert.ThrowsAnyAsync<NcLinkException>(() =>
            client.QueryItemAsync("NO_SUCH_DEVICE_99999", DataItemRequest.At("/MACHINE/STATUS")));
    }

    [Fact]
    public async Task QueryUnknownPath_ShouldThrowModelPathNotFound()
    {
        if (_skip) return;

        await using var client = _fixture.CreateClient();

        await Assert.ThrowsAsync<NcLinkModelPathNotFoundException>(() =>
            client.QueryItemAsync(DeviceId, DataItemRequest.At("/MACHINE/NO_SUCH_PATH")));
    }
}

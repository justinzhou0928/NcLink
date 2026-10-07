using System.Diagnostics;
using NcLink.Protocol.Contracts;
using NcLink.Client.Connection.Exceptions;
using NcLink.Client.Devices.Models;
using NcLink.Client.Query.Models;
using NcLink.Client.Devices.Parsers;

namespace NcLink.Tests.Integration;

/// <summary>
/// 诊断型集成测试：把原先命令行工具里的端到端流程固化为可重复执行的测试。
///
/// 覆盖三件事：
/// <list type="number">
/// <item>全链路功能（模型 → 探测 → 查询 → 设置 → 位操作 → 采样 → 文件）；</item>
/// <item>多设备掉线隔离（一台掉线不得影响其余设备）；</item>
/// <item>响应关联（掉线设备的迟到响应不得被后续请求认领）。</item>
/// </list>
///
/// <b>设计约束一：每个测试方法使用自己独占的设备 SN。</b>
/// 本类会主动让设备掉线；若多个用例共用设备，执行顺序就会影响结果。
///
/// <b>设计约束二：每个步骤都包一层带步骤名的断言。</b>
/// 集成测试失败时最贵的是定位成本，错误信息必须直接指出是哪一步。
/// </summary>
public sealed class DiagnosticsTests : IAsyncLifetime
{
    private static int _serial;

    // deviceCount: 0 —— 设备全部由各用例按需创建，避免用例间共享状态
    private readonly BrokerFixture _fixture = new("DIAG", deviceCount: 0);
    private bool _skip;

    public async Task InitializeAsync() => _skip = !await _fixture.StartAsync();

    public async Task DisposeAsync() => await _fixture.DisposeAsync();

    /// <summary>申请一个本进程内唯一、且跨测试运行也不易冲突的设备 SN。</summary>
    private static string NextDeviceId()
        => $"DIAG{Environment.ProcessId % 1000:D3}{Interlocked.Increment(ref _serial):D3}";

    [Fact]
    public async Task FullFlow_ShouldPassEveryStage()
    {
        if (_skip) return;

        var deviceId = await Step("准备设备", async () =>
        {
            var id = NextDeviceId();
            // 本用例会验证采样，因此显式开启
            await _fixture.StartMachineAsync(id, "flow_channel", enableSampling: true, sampleIntervalMs: 200);
            return id;
        });

        #region 1. 模型与路径解析（离线部分）
        await Step("1. 模型与路径解析", async () =>
        {
            var sample = SampleDeviceModel.Parse(deviceId);
            string[] paths =
            [
                "/NC_LINK_ROOT/MACHINE/CONTROLLER/VARIABLE@REG_X",
                "/MACHINE/CONTROLLER/VARIABLE@REG_X",
                "MACHINE/CONTROLLER/VARIABLE@REG_X",
                "VARIABLE@REG_X",
                "/MACHINE/STATUS",
                "/MACHINE/CONTROLLER/PARAMETER",
                "/MACHINE/CONTROLLER/WARNING",
                "/MACHINE/CONTROLLER/VARIABLE@AXIS_0"
            ];
            var unresolved = paths.Where(p => sample.Resolve(p) is null).ToArray();
            Assert.True(unresolved.Length == 0, $"未能解析：{string.Join(", ", unresolved)}");
            await Task.CompletedTask;
        });

        await using var client = _fixture.CreateClient();

        await Step("2. 连接 MQTT", async () =>
        {
            await client.InitializeAsync();
            Assert.Equal(NcLinkConnectionState.Connected, client.ConnectionState);
        });

        await Step("3. 探测设备", async () =>
        {
            var model = await client.ProbeAsync(deviceId);
            Assert.NotEmpty(model.Nodes);
            Assert.NotEmpty(model.SampleChannels);
        });

        await Step("4. 查询寄存器", async () =>
        {
            var registers = await client.QueryItemAsync(deviceId,
                DataItemRequest.At("/MACHINE/CONTROLLER/VARIABLE@REG_X", 0, 9));
            Assert.True(registers.Succeeded);
            Assert.Equal(10, registers.Count);
        });

        await Step("5. 系统数据 / 报警", async () =>
        {
            var system = await client.QueryItemAsync(deviceId,
                DataItemRequest.At("/MACHINE/CONTROLLER/VARIABLE@SYS", 11, 14));
            Assert.True(system.Succeeded);
            Assert.Contains(system.ToStringArray(), v => v is not null && v.Contains("HNC8"));

            var warning = await client.QueryItemAsync(deviceId, DataItemRequest.At("/MACHINE/CONTROLLER/WARNING"));
            Assert.True(warning.Succeeded);
        });

        await Step("6. 设置与位操作", async () =>
        {
            Assert.True(await client.SetItemAsync(deviceId,
                DataItemRequest.Set("/MACHINE/CONTROLLER/VARIABLE@REG_X", 123, index: 10)));

            Assert.True(await client.SetItemAsync(deviceId,
                DataItemRequest.Set("/MACHINE/CONTROLLER/VARIABLE@REG_X", 1, index: 0, bitOffset: 3)));

            var readback = await client.QueryItemAsync(deviceId,
                DataItemRequest.At("/MACHINE/CONTROLLER/VARIABLE@REG_X", 0, 10));
            Assert.Equal(8, readback.IntAt(0));      // 0 | (1 << 3)
            Assert.Equal(123, readback.IntAt(10));
        });

        await Step("7. 采样订阅", async () =>
        {
            var subscription = await client.SubscribeSampleAsync(deviceId, "flow_channel");

            var frames = new List<SampleFrame>();
            var gate = new object();
            var firstFrame = new TaskCompletionSource<SampleFrame>(TaskCreationOptions.RunContinuationsAsynchronously);

            subscription.FrameReceived += (_, frame) =>
            {
                lock (gate) frames.Add(frame);
                firstFrame.TrySetResult(frame);
            };

            // 等"第一帧到达"这个信号 + 超时，而不是固定 sleep：
            // 固定延时在负载高时会偶发失败（历史上就吃过这个亏）。
            var received = await firstFrame.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(deviceId, received.DeviceId);
            Assert.Equal("flow_channel", received.ChannelId);

            await subscription.UnsubscribeAsync();

            List<SampleFrame> snapshot;
            lock (gate) snapshot = frames.ToList();

            Assert.NotEmpty(snapshot);
            Assert.All(snapshot, frame => Assert.Equal(deviceId, frame.DeviceId));
        });

        await Step("8. 文件上下行", async () =>
        {
            var files = client.Files;

            var names = await files.ListFilesAsync(deviceId);
            Assert.NotEmpty(names);

            var entries = await files.GetFileEntriesAsync(deviceId, [names[0]]);
            Assert.NotEmpty(entries);

            var size = entries[0].Size;
            Assert.True(size > 0, $"文件 {names[0]} 大小应为正，实际 {size}");

            var content = await files.DownloadTextAsync(deviceId, names[0], size);
            Assert.Contains("M30", content);

            const string target = "DIAG_PROG";
            const string body = "%100\n(中文注释)\nM30\n%\n";
            await files.UploadTextAsync(deviceId, target, body);

            var readback = await files.DownloadTextAsync(deviceId, target);
            Assert.Contains("中文注释", readback);
        });

        await Step("9. 异常路径", async () =>
        {
            await Assert.ThrowsAsync<NcLinkModelPathNotFoundException>(() =>
                client.QueryItemAsync(deviceId, DataItemRequest.At("/MACHINE/NO_SUCH_PATH")));

            await Assert.ThrowsAnyAsync<NcLinkException>(() =>
                client.QueryItemAsync("NO_SUCH_SN_99999", DataItemRequest.At("/MACHINE/STATUS")));
        });
    }

    [Fact]
    public async Task DeviceDropout_ShouldNotAffectOtherDevices()
    {
        if (_skip) return;

        var victim = await CreateMachineAsync("victim");

        var survivors = new List<string>();
        for (var i = 0; i < 2; i++)
            survivors.Add(await CreateMachineAsync($"survivor{i}"));

        await using var client = _fixture.CreateClient(timeoutMs: 1500);
        await client.InitializeAsync();

        await Step("探测全部设备", async () =>
        {
            foreach (var deviceId in new[] { victim }.Concat(survivors))
                await client.ProbeAsync(deviceId);
        });

        var baseline = new Dictionary<string, long>();
        await Step("记录存活者基线延迟", async () =>
        {
            foreach (var survivor in survivors)
            {
                var stopwatch = Stopwatch.StartNew();
                await client.QueryItemAsync(survivor, DataItemRequest.At("/MACHINE/STATUS"));
                stopwatch.Stop();
                baseline[survivor] = stopwatch.ElapsedMilliseconds;
            }
        });

        await Step($"让 {victim} 掉线", async () =>
        {
            await _fixture.GetMachine(victim).StopAsync();
            await Task.Delay(1500);
        });

        await Step("掉线设备必须失败", async () =>
        {
            await Assert.ThrowsAnyAsync<NcLinkException>(() =>
                client.QueryItemAsync(victim, DataItemRequest.At("/MACHINE/STATUS")));
        });

        await Step("存活设备必须不受影响", async () =>
        {
            foreach (var survivor in survivors)
            {
                var stopwatch = Stopwatch.StartNew();
                var result = await client.QueryItemAsync(survivor, DataItemRequest.At("/MACHINE/STATUS"));
                stopwatch.Stop();

                Assert.True(result.Succeeded, $"设备 {survivor} 不应受 {victim} 掉线影响");
                Assert.True(stopwatch.ElapsedMilliseconds < 1000,
                    $"设备 {survivor} 延迟异常：{stopwatch.ElapsedMilliseconds}ms（基线 {baseline[survivor]}ms）");
            }
        });
    }

    [Fact]
    public async Task StaleResponseFromDroppedDevice_ShouldNotBeClaimed()
    {
        if (_skip) return;

        var deviceId = await CreateMachineAsync("stale");

        await using var client = _fixture.CreateClient(timeoutMs: 800);
        await client.InitializeAsync();

        await Step("基线：探测并查询一次", async () =>
        {
            await client.ProbeAsync(deviceId);
            Assert.True((await client.QueryItemAsync(deviceId,
                DataItemRequest.At("/MACHINE/STATUS"))).Succeeded);
        });

        await Step("让设备掉线", async () =>
        {
            await _fixture.GetMachine(deviceId).StopAsync();
            await Task.Delay(1500);
        });

        await Step("连续查询 8 次都必须失败（不得认领迟到响应）", async () =>
        {
            for (var i = 0; i < 8; i++)
            {
                await Assert.ThrowsAnyAsync<NcLinkException>(() =>
                    client.QueryItemAsync(deviceId, DataItemRequest.At("/MACHINE/STATUS")));
            }
        });
    }

    /// <summary>启动一台供本用例独占的设备，返回其 SN。</summary>
    private async Task<string> CreateMachineAsync(string purpose)
    {
        var deviceId = NextDeviceId();
        await _fixture.StartMachineAsync(deviceId, $"{purpose}_channel");
        return deviceId;
    }

    /// <summary>
    /// 给每个步骤包上步骤名：失败时错误信息里能直接看到是哪一步出问题。
    /// </summary>
    private static async Task Step(string name, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Xunit.Sdk.XunitException)
        {
            throw;   // 断言失败自带上下文，原样抛出
        }
        catch (Exception e)
        {
            Assert.Fail($"步骤「{name}」失败：{e.GetType().Name}: {e.Message}");
        }
    }

    private static async Task<T> Step<T>(string name, Func<Task<T>> action)
    {
        try
        {
            return await action();
        }
        catch (Exception e)
        {
            Assert.Fail($"步骤「{name}」失败：{e.GetType().Name}: {e.Message}");
            throw;
        }
    }
}
        #endregion

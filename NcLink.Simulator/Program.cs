using System.Text;
using NcLink.Client.Devices.Models;
using NcLink.Client.Devices.Parsers;
using NcLink.Protocol;
using NcLink.Simulator;

// NC-Link 设备侧模拟器：在 MQTT 上模拟若干台数控机床，供离线联调。
// 参数说明见 README「离线联调」一节。

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

string? Arg(string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

bool Flag(string name) => args.Contains(name);

var host = Arg("--host") ?? "127.0.0.1";
var port = int.TryParse(Arg("--port"), out var parsedPort) ? parsedPort : 1883;
var user = Arg("--user");
var password = Arg("--password");
var count = int.TryParse(Arg("--count"), out var parsedCount) ? Math.Max(1, parsedCount) : 2;
var prefix = Arg("--prefix") ?? "STRESS_TEST_";
var channelSuffixMarker = Arg("--channel-suffix");
var echoCorrelationId = !Flag("--no-echo-id");
var pidDir = Arg("--pid-dir");

var explicitIds = Arg("--sn")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

var deviceIds = explicitIds is { Length: > 0 }
    ? explicitIds
    : Enumerable.Range(0, count).Select(i => $"{prefix}{i:D5}").ToArray();

#region 模型

var modelPath = Path.Combine(AppContext.BaseDirectory, "model.json");
var modelJson = File.Exists(modelPath) ? await File.ReadAllTextAsync(modelPath) : EmbeddedSampleModel.Json;

var validationModel = DeviceModelParser.Parse(modelJson, deviceIds[0]);
Console.WriteLine($"模型：{validationModel.Nodes.Count} 个节点，{validationModel.SampleChannels.Count} 个采样通道");
foreach (var channel in validationModel.SampleChannels.Values)
    Console.WriteLine($"  通道 {channel.ChannelId}：周期 {channel.SampleInterval?.TotalMilliseconds:0}ms，{channel.ItemIds.Count} 项");

foreach (var path in new[] { "/MACHINE/STATUS", "/MACHINE/CONTROLLER/VARIABLE@REG_X" })
    Console.WriteLine($"  路径解析 {path} → {validationModel.FindId(path) ?? "未找到"}");

#endregion

#region 采样通道

SimulatedSampleChannel BuildChannel(string channelId, int intervalMs, string[] itemIds, string suffix) => new()
{
    ChannelId = channelId + suffix,
    SampleIntervalMs = intervalMs,
    ItemIds = itemIds
};

#endregion

#region 启动

var machines = new List<SimulatedMachine>();
foreach (var deviceId in deviceIds)
{
    // 多台设备共存时给通道 id 加 SN 后缀，避免客户端无法归因采样数据
    var suffix = channelSuffixMarker is null ? string.Empty : channelSuffixMarker + deviceId;

    var channels = new List<SimulatedSampleChannel>
    {
        BuildChannel("sample_channel0", 1000, ["010302", "010303", "010305", "010307", "01035411", "01035412"], suffix),
        BuildChannel("sample_channel1", 25, ["0103502001", "0103502201"], suffix)
    };

    machines.Add(new SimulatedMachine(new SimulatedMachineOptions
    {
        DeviceId = deviceId,
        Host = host,
        Port = port,
        UserName = user,
        Password = password,
        ModelJson = modelJson,
        EchoCorrelationId = echoCorrelationId,
        SampleChannels = channels
    }));
}

Console.WriteLine();
Console.WriteLine($"正在连接 MQTT {host}:{port}，启动 {machines.Count} 台模拟机床…");

foreach (var machine in machines)
{
    try
    {
        await machine.StartAsync();
        Console.WriteLine($"  [{machine.DeviceId}] connected");
    }
    catch (Exception e)
    {
        Console.Error.WriteLine($"  [{machine.DeviceId}] 连接失败：{e.Message}");
    }
}

if (!string.IsNullOrEmpty(pidDir))
{
    Directory.CreateDirectory(pidDir);
    foreach (var deviceId in deviceIds)
        await File.WriteAllTextAsync(Path.Combine(pidDir, deviceId + ".pid"), Environment.ProcessId.ToString());
    Console.WriteLine($"PID 文件已写入 {pidDir}");
}

Console.WriteLine();
Console.WriteLine("模拟机床已就绪。Ctrl+C 退出。");
Console.WriteLine("  请求主题：Query/Request/{SN}、Set/Request/{SN}、Probe/Query/Request/{SN}");
Console.WriteLine("  采样发布：Sample/{channelId}");

using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    shutdown.Cancel();
};

try
{
    await Task.Delay(Timeout.Infinite, shutdown.Token);
}
catch (OperationCanceledException) { }

Console.WriteLine();
Console.WriteLine("正在停止…");
foreach (var machine in machines)
{
    Console.WriteLine($"  [{machine.DeviceId}] 探测 {machine.ProbeCount}／查询 {machine.QueryCount}／"
                      + $"设置 {machine.SetCount}／采样 {machine.PublishedSampleCount} 帧");

#endregion

}

foreach (var machine in machines) await machine.DisposeAsync();

return 0;

using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NcLink.Client;
using NcLink.Client.Query.Models;
using NcLink;
using NcLink.Protocol.Models;
using NcLink.Transport.Mqtt;

namespace NcLink.Conformance;

/// <summary>
/// 一致性验证宿主程序：对<b>同一台官方模拟设备</b>、用<b>两条官方路径</b>取同一份数据，
/// 逐项比对结果是否一致。
///
/// <list type="number">
/// <item><b>官方路径</b>：HTTP 调 apiServer（<c>/v1/{device_id}/data/</c>，
///   契约取自 apiServer 自带的 Swagger 与《应用开发指导手册》5.1/5.2）；</item>
/// <item><b>本项目路径</b>：用 <c>NcLink</c> 库经 MQTT 直连设备。</item>
/// </list>
///
/// 两条路径最终都要与同一台设备对话，因此<b>语义结果必须一致</b>。
/// 不一致即说明本项目的报文格式或参数语义与官方不符。
///
/// 前置条件：
/// <list type="bullet">
/// <item>EMQX 在 <c>127.0.0.1:1883</c>；</item>
/// <item>官方模拟器 <c>StressTest-INC-Cloud.exe</c> 已连接（10 台 STRESS_TEST_000xx）；</item>
/// <item>apiServer 已启动并连到同一 EMQX（<c>java -jar nclink-api-server-0.3.10.jar</c>）。</item>
/// </list>
///
/// 用法：
/// <code>
/// dotnet run --project tools/NcLink.Conformance
/// dotnet run --project tools/NcLink.Conformance -- --sn STRESS_TEST_00001 --api http://127.0.0.1:19001
/// </code>
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var sn = Arg(args, "--sn") ?? "STRESS_TEST_00000";
        var apiBase = Arg(args, "--api") ?? "http://127.0.0.1:19001";
        var broker = Arg(args, "--broker") ?? "tcp://127.0.0.1:1883";

        // 模式二：官方 apiServer 与我们的 NcLink.ApiServer 逐字符对照。
        // 这是最强的验证——两条独立实现的 HTTP 服务打同一台设备。
        if (Arg(args, "--compare") is { } oursBase)
            return await CompareApiServersAsync(apiBase, oursBase, sn);

        Console.WriteLine("NC-Link 一致性验证：官方 apiServer(HTTP) vs 本项目(MQTT 直连)");
        Console.WriteLine($"  设备 SN    : {sn}");
        Console.WriteLine($"  apiServer  : {apiBase}");
        Console.WriteLine($"  Broker     : {broker}");
        Console.WriteLine();

        // 本项目：走官方推荐的 DI 装配方式（宿主程序）
        var builder = Host.CreateApplicationBuilder(args);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.AddNcLinkMqtt(o =>
        {
            o.BrokerAddress = broker;
            o.ClientId = $"conformance-{Guid.NewGuid():N}"[..24];
        });
        builder.Services.AddNcLink(o => o.RequestTimeout = TimeSpan.FromSeconds(8));

        using var host = builder.Build();
        await host.StartAsync();

        var client = host.Services.GetRequiredService<INcLinkClient>();
        using var api = new ApiServerClient(apiBase, sn);

        var report = new Report();

        try
        {
            await RunAsync(client, api, sn, report);
        }
        catch (Exception e)
        {
            Console.WriteLine($"\n执行中断：{e.GetType().Name}: {e.Message}");
            report.Fail("运行", e.Message);
        }
        finally
        {
            await host.StopAsync();
        }

        return report.Print();
    }

    private static async Task RunAsync(INcLinkClient client, ApiServerClient api, string sn, Report report)
    {
        Console.WriteLine("[1] 取设备模型");
        var apiModel = await api.GetModelAsync();
        var libModel = await client.GetModelAsync(sn);

        report.Check("模型可取得", apiModel is not null && libModel is not null,
            $"api={apiModel?.Length ?? 0} 字节, lib 设备 id={libModel?.DeviceId}");

        // 结构比对：两边从同一份模型里识别出的采样通道应当一致
        var apiChannels = ApiServerClient.SampleChannelIds(apiModel);
        var libChannels = libModel?.SampleChannels.Keys
            .OrderBy(x => x, StringComparer.Ordinal).ToArray() ?? [];
        report.Check("采样通道集合一致",
            apiChannels.Length > 0 && apiChannels.SequenceEqual(libChannels),
            $"api=[{string.Join(",", apiChannels)}]  lib=[{string.Join(",", libChannels)}]");

        const string RegX = "/MACHINE/CONTROLLER/VARIABLE@REG_X";

        Console.WriteLine("\n[2] 查询数据项 get_value（两边用等价参数：索引 0,1,2,3）");
        var apiValues = await api.QueryAsync("get_value", RegX, index: new[] { 0, 1, 2, 3 });
        // At(path, params int[]) 生成 4 个独立索引 0,1,2,3；
        // 与 apiServer 的 index:[0,1,2,3] 是同一语义的两种编码
        var libResult = await client.QueryItemAsync(sn, DataItemRequest.At(RegX, 0, 1, 2, 3));

        report.Check("查询均成功", apiValues is not null && libResult.Succeeded,
            $"api={Describe(apiValues)}, lib=code={(libResult.Succeeded ? "OK" : "NG")} values={libResult.Values.Count}");

        var libNumbers = libResult.ToIntArray();
        report.Check("取值一致", SameNumbers(apiValues, libNumbers),
            $"api={Describe(apiValues)}  lib=[{string.Join(",", libNumbers)}]");

        Console.WriteLine("\n[3] 查询长度 get_length");
        var apiLen = await api.QueryAsync("get_length", RegX, index: null);
        var libLen = await client.QueryItemAsync(sn, new DataItemRequest
        {
            Path = RegX,
            Operation = NcLinkOperation.GetLength
        });

        var apiLenNums = Flatten(apiLen);
        var libLenNums = libLen.ToIntArray();
        report.Check("长度一致",
            apiLenNums.Length > 0 && libLenNums.Length > 0 && apiLenNums[0] == libLenNums[0],
            $"api={Describe(apiLen)}  lib=[{string.Join(",", libLenNums)}]");

        Console.WriteLine("\n[4] 设值 set_value");
        const int NewValue = 777;
        var apiSet = await api.SetAsync(RegX, index: 0, value: NewValue);
        var libSet = await client.SetItemAsync(sn, DataItemRequest.Set(RegX, NewValue, index: 0));

        report.Check("设值均受理", apiSet && libSet, $"api={apiSet}, lib={libSet}");

        // 回读确认
        var apiAfter = await api.QueryAsync("get_value", RegX, index: 0);
        var libAfter = await client.QueryItemAsync(sn, DataItemRequest.At(RegX, 0, 0));
        var apiAfterNums = Flatten(apiAfter);
        var libAfterNums = libAfter.ToIntArray();
        report.Check("回读值一致",
            apiAfterNums.Length > 0 && libAfterNums.Length > 0 && apiAfterNums[0] == libAfterNums[0],
            $"api={Describe(apiAfter)}  lib=[{string.Join(",", libAfterNums)}]");

        Console.WriteLine("\n[5] 模型路径解析（库侧）");
        var regY = await client.QueryItemAsync(sn, DataItemRequest.At("/MACHINE/CONTROLLER/VARIABLE@REG_Y", 0, 3));
        report.Check("另一数据项 REG_Y 也可按路径取到", regY.Succeeded,
            $"values=[{string.Join(",", regY.ToIntArray())}]");

        Console.WriteLine("\n[6] 采样（主题 Sample/{SN}/{channelId}）");
        var channelId = libModel?.SampleChannels.Keys.FirstOrDefault();
        if (channelId is null)
        {
            report.Fail("采样", "模型里没有采样通道");
            return;
        }

        // 库侧：订阅并等帧。这是本次修复的核心——
        // 主题必须带 SN，且通配订阅必须用 Sample/#（+ 只匹配一层）
        var sub = await client.SubscribeSampleAsync(sn, channelId);
        var frameTcs = new TaskCompletionSource<SampleFrame>();
        sub.FrameReceived += (_, f) => frameTcs.TrySetResult(f);

        var gotFrame = await Task.WhenAny(frameTcs.Task, Task.Delay(TimeSpan.FromSeconds(20))) == frameTcs.Task;
        report.Check("库侧收到采样帧", gotFrame,
            gotFrame
                ? $"通道={frameTcs.Task.Result.ChannelId} 项数={frameTcs.Task.Result.Items.Count}"
                : $"20 秒内未收到（通道 {channelId}）");

        if (gotFrame)
        {
            var frame = frameTcs.Task.Result;
            report.Check("帧归属设备正确", frame.DeviceId == sn,
                $"frame.DeviceId={frame.DeviceId}, sn={sn}");
            report.Check("数据项映射来自模型通道配置", frame.ItemIds.Count > 0,
                $"ItemIds={frame.ItemIds.Count} 项, Items={frame.Items.Count} 项");
        }

        await sub.UnsubscribeAsync();

        // apiServer 侧：同一通道也应能订阅并取到数据
        var apiSubId = await api.SubscribeSampleAsync(channelId);
        report.Check("apiServer 侧也能订阅同一通道", !string.IsNullOrEmpty(apiSubId), $"sub_id={apiSubId}");

        if (!string.IsNullOrEmpty(apiSubId))
        {
            await Task.Delay(TimeSpan.FromSeconds(8));
            var apiSample = await api.GetSampleAsync(apiSubId);
            report.Check("apiServer 侧能取到采样数据", apiSample is not null,
                apiSample is null ? "(无数据)" : $"{apiSample.Length} 字符");
            await api.UnsubscribeSampleAsync(apiSubId);
        }
    }


    /// <summary>
    /// 模式二：官方 apiServer vs 我们的 NcLink.ApiServer。
    /// </summary>
    private static async Task<int> CompareApiServersAsync(string officialBase, string oursBase, string sn)
    {
        Console.WriteLine("==================================================");
        Console.WriteLine("ApiServer 对照：官方 Java 实现 vs 我们的 C# 实现");
        Console.WriteLine($"  设备 SN: {sn}");
        Console.WriteLine("==================================================");
        Console.WriteLine();

        var comparison = new ApiServerComparison(officialBase, oursBase, sn);

        Console.WriteLine("[一] 数据接口 /v1/{sn}/data/（逐字符比对）");
        Console.WriteLine();
        var dataFailures = await comparison.RunAsync(Console.Out);

        Console.WriteLine();
        Console.WriteLine("[二] 采样接口 /v1/sample/{sn}/...");
        Console.WriteLine();
        var sampleFailures = await comparison.RunSampleAsync(Console.Out);

        Console.WriteLine();
        Console.WriteLine("[三] 文档端点 / 监听端口 / 文件端点");
        Console.WriteLine();
        var surface = new DocsAndSurfaceComparison(officialBase, oursBase, sn);
        var surfaceFailures = await surface.RunAsync(Console.Out);

        Console.WriteLine();
        Console.WriteLine("[四] 行为面：v2 端点 / 4xx 错误体 / 路径变体 / 多设备");
        Console.WriteLine();
        var behavior = new BehaviorComparison(officialBase, oursBase, sn);
        var behaviorFailures = await behavior.RunAsync(Console.Out);

        var total = dataFailures + sampleFailures + surfaceFailures + behaviorFailures;
        Console.WriteLine();
        Console.WriteLine("==================================================");
        Console.WriteLine(total == 0
            ? "两个 ApiServer 的行为一致。"
            : $"存在 {total} 处不一致，见上方明细。");
        Console.WriteLine("==================================================");

        return total == 0 ? 0 : 1;
    }

    /// <summary>apiServer 的 value 是嵌套数组（每个 item 一层），拍平后比数值。</summary>
    private static int[] Flatten(JsonElement? value)
    {
        if (value is not { } v) return [];
        var list = new List<int>();
        void Walk(JsonElement e)
        {
            if (e.ValueKind == JsonValueKind.Array)
                foreach (var c in e.EnumerateArray()) Walk(c);
            else if (e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var i))
                list.Add(i);
        }
        Walk(v);
        return [.. list];
    }

    private static bool SameNumbers(JsonElement? apiValue, int[] libValues)
    {
        var a = Flatten(apiValue);
        return a.Length == libValues.Length && a.SequenceEqual(libValues);
    }

    private static int? FirstNumber(int[] values) => values.Length > 0 ? values[0] : null;

    private static string Describe(JsonElement? value)
        => value is null ? "(null)" : $"[{string.Join(",", Flatten(value))}]";

    private static string? Arg(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}

/// <summary>
/// 按《应用开发指导手册》5.1/5.2 与 apiServer 自带 Swagger 的契约调用 HTTP 接口。
/// </summary>
internal sealed class ApiServerClient(string baseUrl, string sn) : IDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>GET /v1/{device_id}/model —— 取设备模型。</summary>
    public async Task<string?> GetModelAsync()
    {
        try
        {
            var r = await _http.GetAsync($"{baseUrl}/v1/{sn}/model").ConfigureAwait(false);
            return await r.Content.ReadAsStringAsync().ConfigureAwait(false);
        }
        catch (Exception) { return null; }
    }

    /// <summary>从模型响应里取根 id（用于与库侧比对）。</summary>
    public string? TryGetModelRootId()
    {
        try
        {
            var text = GetModelAsync().GetAwaiter().GetResult();
            if (string.IsNullOrEmpty(text)) return null;
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.TryGetProperty("value", out var v) &&
                v.TryGetProperty("id", out var id))
                return id.GetString();
        }
        catch (Exception) { }
        return null;
    }

    /// <summary>从 apiServer 返回的模型 JSON 里取出采样通道 id（递归找 SAMPLE_CHANNEL 节点）。</summary>
    public static string[] SampleChannelIds(string? modelResponse)
    {
        if (string.IsNullOrEmpty(modelResponse)) return [];
        try
        {
            using var doc = JsonDocument.Parse(modelResponse);
            if (!doc.RootElement.TryGetProperty("value", out var root)) return [];
            var ids = new List<string>();
            Walk(root, "SAMPLE_CHANNEL", ids);
            return [.. ids.OrderBy(x => x, StringComparer.Ordinal)];
        }
        catch (Exception) { return []; }
    }

    /// <summary>递归统计某 type 的节点数。</summary>
    public static int CountByType(string? modelResponse, string type)
    {
        if (string.IsNullOrEmpty(modelResponse)) return 0;
        try
        {
            using var doc = JsonDocument.Parse(modelResponse);
            if (!doc.RootElement.TryGetProperty("value", out var root)) return 0;
            var ids = new List<string>();
            Walk(root, type, ids);
            return ids.Count;
        }
        catch (Exception) { return 0; }
    }

    private static void Walk(JsonElement node, string type, List<string> hits)
    {
        if (node.ValueKind != JsonValueKind.Object) return;
        if (node.TryGetProperty("type", out var t) &&
            string.Equals(t.GetString(), type, StringComparison.Ordinal) &&
            node.TryGetProperty("id", out var id) && id.GetString() is { } s)
            hits.Add(s);

        foreach (var key in new[] { "devices", "components", "dataItems", "configs" })
            if (node.TryGetProperty(key, out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var c in arr.EnumerateArray()) Walk(c, type, hits);
    }

    /// <summary>
    /// POST /v1/{device_id}/data/。
    ///
    /// <paramref name="index"/> 可为单个整数或整数数组，与官方 <c>Item.index</c>（Object）一致：
    /// 实测 apiServer 接受 <c>[0,1,2,3]</c> 这类显式索引数组，
    /// 但不接受 <c>"0-7"</c> 区间字符串。
    /// </summary>
    public Task<JsonElement?> QueryAsync(string operation, string path, object? index)
        => PostDataAsync(operation, new Dictionary<string, object?>
        {
            ["path"] = path,
            ["index"] = index
        });

    /// <summary>POST /v1/{device_id}/data/ —— set_value。</summary>
    public async Task<bool> SetAsync(string path, int index, object value)
    {
        var v = await PostDataAsync("set_value", new Dictionary<string, object?>
        {
            ["path"] = path,
            ["index"] = index,
            ["value"] = value
        }).ConfigureAwait(false);
        return v is not null;
    }

    /// <summary>POST /v1/sample/{device_id}/sub/{channel_id} —— 订阅采样，返回 sub_id。</summary>
    public async Task<string?> SubscribeSampleAsync(string channelId)
    {
        try
        {
            var r = await _http.PostAsync(
                $"{baseUrl}/v1/sample/{sn}/sub/{channelId}",
                new StringContent("{}", Encoding.UTF8, "application/json")).ConfigureAwait(false);
            var text = await r.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.TryGetProperty("value", out var v) ? v.GetString() : null;
        }
        catch (Exception) { return null; }
    }

    /// <summary>GET /v1/sample/{device_id}/data/{sub_id} —— 取服务端缓存的采样数据。</summary>
    public async Task<string?> GetSampleAsync(string subId)
    {
        try
        {
            var r = await _http.GetAsync($"{baseUrl}/v1/sample/{sn}/data/{subId}").ConfigureAwait(false);
            var text = await r.Content.ReadAsStringAsync().ConfigureAwait(false);
            return text.Contains("\"value\":null", StringComparison.Ordinal) ? null : text;
        }
        catch (Exception) { return null; }
    }

    /// <summary>POST /v1/sample/{device_id}/unsub/{sub_id} —— 取消订阅。</summary>
    public async Task UnsubscribeSampleAsync(string subId)
    {
        try
        {
            await _http.PostAsync(
                $"{baseUrl}/v1/sample/{sn}/unsub/{subId}",
                new StringContent("{}", Encoding.UTF8, "application/json")).ConfigureAwait(false);
        }
        catch (Exception) { /* 尽力而为 */ }
    }

    private async Task<JsonElement?> PostDataAsync(string operation, Dictionary<string, object?> item)
    {
        // 去掉 null 字段：官方 Item 的各字段都可选
        foreach (var k in item.Where(kv => kv.Value is null).Select(kv => kv.Key).ToArray())
            item.Remove(k);

        var body = new
        {
            operation,
            items = new[] { item },
            timeout = 8000
        };

        try
        {
            var r = await _http.PostAsJsonAsync($"{baseUrl}/v1/{sn}/data/", body).ConfigureAwait(false);
            var text = await r.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!r.IsSuccessStatusCode) return null;

            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;

            // DataResponse: { code:int, status:string, value:object }
            var status = root.TryGetProperty("status", out var st) ? st.GetString() : null;
            if (!string.Equals(status, "SUCCESS", StringComparison.OrdinalIgnoreCase)) return null;

            return root.TryGetProperty("value", out var val) ? val.Clone() : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>逐项记录并汇总。</summary>
internal sealed class Report
{
    private int _pass;
    private int _fail;
    private readonly List<string> _failures = [];

    public void Check(string label, bool ok, string detail)
    {
        if (ok) { _pass++; Console.WriteLine($"  [一致] {label}  {detail}"); }
        else
        {
            _fail++;
            _failures.Add($"{label}: {detail}");
            Console.WriteLine($"  [不一致] {label}  {detail}");
        }
    }

    public void Fail(string label, string detail)
    {
        _fail++;
        _failures.Add($"{label}: {detail}");
        Console.WriteLine($"  [失败] {label}  {detail}");
    }

    public int Print()
    {
        Console.WriteLine();
        Console.WriteLine(new string('=', 68));
        Console.WriteLine($"一致性验证：一致 {_pass} 项，不一致/失败 {_fail} 项");
        if (_failures.Count > 0)
        {
            Console.WriteLine("\n不一致明细：");
            foreach (var f in _failures) Console.WriteLine($"  - {f}");
        }
        else
        {
            Console.WriteLine("本项目与官方 apiServer 在同一设备上的结果完全一致。");
        }
        Console.WriteLine(new string('=', 68));
        return _fail == 0 ? 0 : 1;
    }
}

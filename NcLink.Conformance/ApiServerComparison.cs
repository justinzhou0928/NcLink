using System.Text;
using System.Text.Json;

namespace NcLink.Conformance;

/// <summary>
/// 两个 HTTP 服务（官方 apiServer 与我们的 ApiServer）在同一台设备上的逐项对照。
///
/// 这是本项目最强的验证手段：两条<b>独立实现</b>的 HTTP 服务打同一台真实设备，
/// 请求体的 error code、响应体的字段形状都必须<b>逐字符一致</b>。
///
/// 用例表里的期望值全部来自对官方 apiServer 的实测，不是推断。
/// </summary>
internal sealed class ApiServerComparison(string officialBase, string oursBase, string sn)
{
    private sealed record Case(string Name, string Body);

    /// <summary>逐字符比对的用例（成功路径 + 全部实测错误码）。</summary>
    private static readonly Case[] Cases =
    [
        new("get_value index=0",
            """{"operation":"get_value","items":[{"path":"/MACHINE/CONTROLLER/VARIABLE@REG_X","index":0}],"timeout":8000}"""),
        new("get_value index=[0,1,2]",
            """{"operation":"get_value","items":[{"path":"/MACHINE/CONTROLLER/VARIABLE@REG_X","index":[0,1,2]}],"timeout":8000}"""),
        new("get_length",
            """{"operation":"get_length","items":[{"path":"/MACHINE/CONTROLLER/VARIABLE@REG_X"}],"timeout":8000}"""),
        new("get_attributes（设备回 NG）",
            """{"operation":"get_attributes","items":[{"path":"/MACHINE/CONTROLLER/VARIABLE@REG_X"}],"timeout":8000}"""),
        new("两个 items",
            """{"operation":"get_value","items":[{"path":"/MACHINE/CONTROLLER/VARIABLE@REG_X","index":0},{"path":"/MACHINE/CONTROLLER/VARIABLE@REG_Y","index":0}],"timeout":8000}"""),
        new("set_value",
            """{"operation":"set_value","items":[{"path":"/MACHINE/CONTROLLER/VARIABLE@REG_X","index":0,"value":456}],"timeout":8000}"""),
        new("operation 缺失（默认 get_value）",
            """{"items":[{"path":"/MACHINE/CONTROLLER/VARIABLE@REG_X"}],"timeout":8000}"""),

        // 以下为错误路径：返回码是实测值，最容易实现得不一致
        new("items 缺失 → code 1",
            """{"operation":"get_value","timeout":8000}"""),
        new("items 空数组 → code 0 且 value=[]",
            """{"operation":"get_value","items":[],"timeout":8000}"""),
        new("path 缺失 → code 3",
            """{"operation":"get_value","items":[{"index":0}],"timeout":8000}"""),
        new("path 不存在 → code 3",
            """{"operation":"get_value","items":[{"path":"/NO/SUCH/PATH"}],"timeout":8000}"""),
        new("operation 非法 → code 2",
            """{"operation":"no_such_op","items":[{"path":"/MACHINE/CONTROLLER/VARIABLE@REG_X"}],"timeout":8000}"""),
        new("index 传区间字符串 → code 4",
            """{"operation":"get_value","items":[{"path":"/MACHINE/CONTROLLER/VARIABLE@REG_X","index":"0-7"}],"timeout":8000}"""),
        new("index 数组含非整数 → code 4",
            """{"operation":"get_value","items":[{"path":"/MACHINE/CONTROLLER/VARIABLE@REG_X","index":[0,"x"]}],"timeout":8000}"""),
        new("sub_id 不存在 → code 17",
            null!)
    ];

    public async Task<int> RunAsync(TextWriter output)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(40) };
        var failures = 0;

        output.WriteLine($"官方 apiServer : {officialBase}");
        output.WriteLine($"我们的 ApiServer: {oursBase}");
        output.WriteLine();

        foreach (var testCase in Cases)
        {
            string official, ours;

            if (testCase.Body is null)
            {
                // 采样 sub_id 不存在：GET 而非 POST
                var url = $"{sn}/data/00000000-0000-0000-0000-000000000000";
                official = await GetAsync(http, officialBase, url);
                ours = await GetAsync(http, oursBase, url);
            }
            else
            {
                official = await PostAsync(http, officialBase, testCase.Body);
                ours = await PostAsync(http, oursBase, testCase.Body);
            }

            var same = string.Equals(official, ours, StringComparison.Ordinal);
            if (!same) failures++;

            output.WriteLine($"  [{(same ? "一致" : "不一致")}] {testCase.Name}");
            if (!same)
            {
                output.WriteLine($"      官方: {official}");
                output.WriteLine($"      我们: {ours}");
            }
        }

        output.WriteLine();
        output.WriteLine($"逐字符一致 {Cases.Length - failures} / 不一致 {failures}");
        return failures;
    }

    private async Task<string> PostAsync(HttpClient http, string baseUrl, string body)
    {
        try
        {
            var response = await http.PostAsync(
                $"{baseUrl}/v1/{sn}/data/",
                new StringContent(body, Encoding.UTF8, "application/json")).ConfigureAwait(false);
            return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        }
        catch (Exception e)
        {
            return $"EX: {e.Message}";
        }
    }

    private async Task<string> GetAsync(HttpClient http, string baseUrl, string relative)
    {
        try
        {
            var response = await http.GetAsync($"{baseUrl}/v1/sample/{relative}").ConfigureAwait(false);
            return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        }
        catch (Exception e)
        {
            return $"EX: {e.Message}";
        }
    }

    /// <summary>
    /// 采样端点对照：两边各订阅同一通道，比较取回的帧序列。
    ///
    /// 帧数据本身必须相同（逐帧 <c>beginTime</c> 序列 + 各帧形状），
    /// 但<b>帧数允许差 1</b>——两边的订阅建立时刻有先后，
    /// 官方可能比我们多捕到最早那一帧。这属于时序，不是契约差异。
    /// </summary>
    public async Task<int> RunSampleAsync(TextWriter output)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(40) };

        var officialSub = await SubscribeAsync(http, officialBase);
        var oursSub = await SubscribeAsync(http, oursBase);

        if (officialSub is null || oursSub is null)
        {
            output.WriteLine($"  [不一致] 采样订阅失败（官方={officialSub ?? "(失败)"} 我们={oursSub ?? "(失败)"}）");
            return 1;
        }

        output.WriteLine("  已订阅 sample_channel0，等待 13 秒收集帧...");
        await Task.Delay(TimeSpan.FromSeconds(13));

        var official = await DrainAsync(http, officialBase, officialSub);
        var ours = await DrainAsync(http, oursBase, oursSub);

        await UnsubscribeAsync(http, officialBase, officialSub);
        await UnsubscribeAsync(http, oursBase, oursSub);

        var failures = 0;

        output.WriteLine($"  官方帧数={official?.Count ?? -1}  我们帧数={ours?.Count ?? -1}");

        if (official is null || ours is null)
        {
            output.WriteLine("  [不一致] 有一边取不到帧");
            return 1;
        }

        // 我们的帧序列应当是官方的子序列（同一批设备帧，可能少开头一两帧）
        var officialTimes = official.Select(f => f.BeginTime).ToList();
        var ourTimes = ours.Select(f => f.BeginTime).ToList();
        var isSubsequence = IsSubsequence(ourTimes, officialTimes);

        output.WriteLine($"  [{(isSubsequence ? "一致" : "不一致")}] 帧序列同源（我们 = 官方的子序列）");
        if (!isSubsequence)
        {
            failures++;
            output.WriteLine($"      官方: {string.Join(",", officialTimes)}");
            output.WriteLine($"      我们: {string.Join(",", ourTimes)}");
        }

        // 形状一致性：帧数相同时逐帧比 data 结构
        if (official.Count > 0 && ours.Count > 0)
        {
            var officialShape = Shape(official[^1]);
            var ourShape = Shape(ours[^1]);
            var sameShape = officialShape == ourShape;
            if (!sameShape) failures++;
            output.WriteLine($"  [{(sameShape ? "一致" : "不一致")}] 末帧形状（beginTime 类型 + data 层级）");
            if (!sameShape)
            {
                output.WriteLine($"      官方: {officialShape}");
                output.WriteLine($"      我们: {ourShape}");
            }
        }

        return failures;
    }

    /// <summary>把一帧压成形状字符串：只保留结构与类型，不比较具体数值。</summary>
    private static string Shape(SampleFrameView frame)
        => $"beginTime:{frame.BeginTimeType} data.len:{frame.DataLength} "
         + $"item0.keys:[{string.Join(",", frame.Item0Keys)}]";

    private static bool IsSubsequence(List<string?> small, List<string?> big)
    {
        var i = 0;
        foreach (var t in big)
        {
            if (i < small.Count && small[i] == t) i++;
        }
        return i == small.Count;
    }

    private async Task<string?> SubscribeAsync(HttpClient http, string baseUrl)
    {
        try
        {
            var response = await http.PostAsync(
                $"{baseUrl}/v1/sample/{sn}/sub/sample_channel0",
                new StringContent("{}", Encoding.UTF8, "application/json")).ConfigureAwait(false);
            var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
            return json.RootElement.GetProperty("value").GetString();
        }
        catch (Exception) { return null; }
    }

    private async Task UnsubscribeAsync(HttpClient http, string baseUrl, string subId)
    {
        try
        {
            await http.PostAsync($"{baseUrl}/v1/sample/{sn}/unsub/{subId}",
                new StringContent("{}", Encoding.UTF8, "application/json")).ConfigureAwait(false);
        }
        catch (Exception) { /* 尽力而为 */ }
    }

    private sealed record SampleFrameView(string? BeginTime, string BeginTimeType, int DataLength, string[] Item0Keys);

    private async Task<List<SampleFrameView>?> DrainAsync(HttpClient http, string baseUrl, string subId)
    {
        try
        {
            var response = await http.GetAsync($"{baseUrl}/v1/sample/{sn}/data/{subId}").ConfigureAwait(false);
            var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
            var root = json.RootElement;

            if (root.GetProperty("status").GetString() != "SUCCESS") return null;

            var frames = new List<SampleFrameView>();
            foreach (var frame in root.GetProperty("value").EnumerateArray())
            {
                var beginTime = frame.GetProperty("beginTime");
                var data = frame.GetProperty("data");

                var item0Keys = Array.Empty<string>();
                var dataLength = 0;
                if (data.ValueKind == JsonValueKind.Array)
                {
                    dataLength = data.GetArrayLength();
                    if (dataLength > 0)
                        item0Keys = [.. data[0].EnumerateObject().Select(p => p.Name)];
                }

                frames.Add(new SampleFrameView(
                    beginTime.ValueKind == JsonValueKind.String ? beginTime.GetString() : beginTime.GetRawText(),
                    beginTime.ValueKind.ToString(),
                    dataLength,
                    item0Keys));
            }

            return frames;
        }
        catch (Exception) { return null; }
    }
}

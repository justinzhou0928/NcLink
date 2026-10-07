using System.Text;
using System.Text.Json;

namespace NcLink.Conformance;

/// <summary>
/// 行为面比较：把多轮验证中探到的边界用例固化成可重复的检查表。
///
/// 覆盖 <see cref="ApiServerComparison"/>（数据语义）与
/// <see cref="DocsAndSurfaceComparison"/>（文档/端口/文件）之外的部分：
/// <list type="bullet">
/// <item><b>v2 端点</b>——官方 0.3.10 的 v2 数据接口实测恒定超时（<c>code 16</c>），
///   参数错误一律 <c>code 5</c>；这里验证我们<b>忠实复刻</b>了该行为；</item>
/// <item><b>4xx 错误体</b>——官方对 404/405 返回 Spring 默认错误 JSON，不是空体；</item>
/// <item><b>路径变体与方法</b>——无尾斜杠、深层 catch-all、PUT/DELETE；</item>
/// <item><b>operation 大小写与空值</b>；</item>
/// <item><b>多设备</b>——换 SN 与不存在的 SN。</item>
/// </list>
/// </summary>
internal sealed class BehaviorComparison(string officialBase, string oursBase, string sn)
{
    private sealed record Case(string Label, HttpMethod Method, string PathTemplate, string? Body = null);

    private const string RegX = "/MACHINE/CONTROLLER/VARIABLE@REG_X";

    private static string Data(string op, string items, int timeout = 8000)
        => $$"""{"operation":"{{op}}","items":[{{items}}],"timeout":{{timeout}}}""";

    private static readonly Case[] Cases =
    [
        // v2：官方实测的全部行为
        new("v2 data 缺 items → 5", HttpMethod.Post, "/v2/{sn}/data/", """{"operation":"get_value","timeout":8000}"""),
        new("v2 data operation 非法 → 5", HttpMethod.Post, "/v2/{sn}/data/", Data("no_such_op", """{"path":"/MACHINE/CONTROLLER/VARIABLE@REG_X"}""")),
        new("v2 data path 缺失 → 5", HttpMethod.Post, "/v2/{sn}/data/", Data("get_value", """{"index":0}""")),
        new("v2 data index 字符串 → 5", HttpMethod.Post, "/v2/{sn}/data/", Data("get_value", $$"""{"path":"{{RegX}}","index":"0-7"}""")),
        new("v2 data items 空数组 → 成功空数组", HttpMethod.Post, "/v2/{sn}/data/", Data("get_value", "")),
        new("v2 data 合法请求 → 16（复刻官方超时）", HttpMethod.Post, "/v2/{sn}/data/", Data("get_value", $$"""{"path":"{{RegX}}","index":0}""")),
        new("v2 data GET → 5", HttpMethod.Get, "/v2/{sn}/data/"),
        new("v2 data GET 带 query → 5", HttpMethod.Get, "/v2/{sn}/data/?operation=get_value"),
        new("v2 file 缺参数 → 400", HttpMethod.Get, "/v2/{sn}/file/"),
        new("v2 file path+key（设备无此文件）", HttpMethod.Get, "/v2/{sn}/file/?path=/MACHINE/CONTROLLER/FILE&key=x"),
        new("v2 model", HttpMethod.Get, "/v2/{sn}/model"),
        new("v2 sample 不存在 → 404", HttpMethod.Post, "/v2/sample/{sn}/sub/sample_channel0", "{}"),

        // 4xx 错误体形状
        new("PUT data → 405 带错误体", HttpMethod.Put, "/v1/{sn}/data/"),
        new("DELETE data → 405 带错误体", HttpMethod.Delete, "/v1/{sn}/data/"),
        new("sample data 缺 sub_id → 404 带错误体", HttpMethod.Get, "/v1/sample/{sn}/data/"),

        // 路径变体
        new("data 无尾斜杠", HttpMethod.Post, "/v1/{sn}/data", Data("get_value", $$"""{"path":"{{RegX}}","index":0}""")),
        new("data 深层 catch-all", HttpMethod.Post, "/v1/{sn}/data/extra/deep", Data("get_value", $$"""{"path":"{{RegX}}","index":0}""")),
        new("model 带尾斜杠", HttpMethod.Get, "/v1/{sn}/model/"),

        // operation 大小写与空值 / timeout
        new("operation 大写 GET_VALUE", HttpMethod.Post, "/v1/{sn}/data/", Data("GET_VALUE", $$"""{"path":"{{RegX}}","index":0}""")),
        new("operation 空串", HttpMethod.Post, "/v1/{sn}/data/", Data("", $$"""{"path":"{{RegX}}","index":0}""")),
        new("timeout=0", HttpMethod.Post, "/v1/{sn}/data/", Data("get_value", $$"""{"path":"{{RegX}}","index":0}""", 0)),
        new("timeout 缺失", HttpMethod.Post, "/v1/{sn}/data/", $$"""{"operation":"get_value","items":[{"path":"{{RegX}}","index":0}]}"""),

        // 未测过的 operation
        new("get_keys（FILE 节点）", HttpMethod.Post, "/v1/{sn}/data/", Data("get_keys", """{"path":"/MACHINE/CONTROLLER/FILE"}""")),
        new("get_keys 空 key → 19", HttpMethod.Post, "/v1/{sn}/data/", Data("get_keys", """{"path":"/MACHINE/CONTROLLER/FILE","key":""}""")),
        new("add", HttpMethod.Post, "/v1/{sn}/data/", Data("add", $$"""{"path":"{{RegX}}","index":0,"value":[9]}""")),
        new("delete", HttpMethod.Post, "/v1/{sn}/data/", Data("delete", $$"""{"path":"{{RegX}}","index":0}""")),

        // 采样边界
        new("sub 不存在的通道", HttpMethod.Post, "/v1/sample/{sn}/sub/no_such_channel", "{}"),
        new("unsub 不存在的 sub_id", HttpMethod.Post, "/v1/sample/{sn}/unsub/00000000-0000-0000-0000-000000000000", "{}"),
    ];

    /// <summary>多设备用例：与 <see cref="Cases"/> 分开，因为 SN 不同。</summary>
    private static readonly (string Label, string Sn, string Path, string? Body)[] OtherDevices =
    [
        ("get_value @ 另一台设备", "STRESS_TEST_00003", "/v1/{sn}/data/", Data("get_value", $$"""{"path":"{{RegX}}","index":0}""")),
        ("model @ 另一台设备", "STRESS_TEST_00003", "/v1/{sn}/model", null),
        ("get_value @ 不存在的 SN", "NO_SUCH_DEVICE_999", "/v1/{sn}/data/", Data("get_value", $$"""{"path":"{{RegX}}","index":0}""")),
    ];

    public async Task<int> RunAsync(TextWriter output)
    {
        var failures = 0;
        var total = 0;

        foreach (var c in Cases)
        {
            total++;
            if (!await CompareOneAsync(output, c.Label, c.Method, c.PathTemplate.Replace("{sn}", sn), c.Body))
                failures++;
        }

        foreach (var (label, deviceSn, path, body) in OtherDevices)
        {
            total++;
            if (!await CompareOneAsync(output, label, body is null ? HttpMethod.Get : HttpMethod.Post,
                    path.Replace("{sn}", deviceSn), body))
                failures++;
        }

        output.WriteLine();
        output.WriteLine($"  行为面：一致 {total - failures} / 不一致 {failures}");

        failures += await RunExtraAsync(output);
        return failures;
    }

    /// <summary>
    /// 两组用 JSON 用例表达不了的检查：
    /// <list type="number">
    /// <item><b>multipart 上传</b>的三个表单字段（<c>path</c>/<c>key</c>/<c>value</c>）——
    ///   缺字段时两边都必须是 Spring 形状的 400。</item>
    /// <item><b>v1 GET data 的已知差异</b>——官方只读请求体，而 GET 带不了体，
    ///   于是<b>任何 query 组合都是 <c>code 3</c></b>；我们把查询参数读了起来，
    ///   因此是可用的。<b>这是刻意的改进，显式报出来而不是藏起来。</b></item>
    /// </list>
    /// </summary>
    private async Task<int> RunExtraAsync(TextWriter output)
    {
        var failures = 0;

        output.WriteLine();
        output.WriteLine("  上传表单（multipart）：");

        // 文件名必须唯一：上传会真的写进设备，用固定名字会污染"设备无此文件"那条下载用例
        var probeFile = $"dsh-upload-{Guid.NewGuid():N}.nc";
        var uploadCases = new (string Label, (string Name, string? FileName, string? Value)[] Fields)[]
        {
            ("只给 value", [("value", "a.nc", "hello")]),
            ("只给 path+key", [("path", null, "/MACHINE/CONTROLLER/FILE"), ("key", null, probeFile)]),
            ("path+key+value", [("path", null, "/MACHINE/CONTROLLER/FILE"), ("key", null, probeFile), ("value", "a.nc", "hello")])
        };

        foreach (var (label, fields) in uploadCases)
        {
            var official = await SendMultipartAsync(officialBase, $"/v1/{sn}/file/", fields);
            var ours = await SendMultipartAsync(oursBase, $"/v1/{sn}/file/", fields);
            var same = official.Code == ours.Code && ShapeOf(official.Body) == ShapeOf(ours.Body);
            if (!same) failures++;
            output.WriteLine($"    [{(same ? "一致" : "不一致")}] {label}");
            if (!same)
            {
                output.WriteLine($"        官方: HTTP {official.Code} {Truncate(official.Body)}");
                output.WriteLine($"        我们: HTTP {ours.Code} {Truncate(ours.Body)}");
            }
        }

        output.WriteLine();
        output.WriteLine("  文件下载成功路径（v1，原始字节）：");

        // 这条路径曾经完全没被覆盖——旧的比对用例都打在"文件不存在"上，
        // 而成功时官方回的是原始字节流（不是 DataResponse），我们曾错误地包成 JSON+十六进制。
        var downloadPath = $"/v1/{sn}/file/?path=/MACHINE/CONTROLLER/FILE&key={probeFile}";
        var officialDownload = await SendRawAsync(officialBase, downloadPath);
        var ourDownload = await SendRawAsync(oursBase, downloadPath);
        var downloadSame = officialDownload.Code == ourDownload.Code
            && officialDownload.ContentType == ourDownload.ContentType
            && officialDownload.Body.SequenceEqual(ourDownload.Body);
        if (!downloadSame) failures++;
        output.WriteLine($"    [{(downloadSame ? "一致" : "不一致")}] GET {downloadPath}");
        output.WriteLine($"        官方: HTTP {officialDownload.Code} ct={officialDownload.ContentType ?? "(无)"} {officialDownload.Body.Length} 字节");
        output.WriteLine($"        我们: HTTP {ourDownload.Code} ct={ourDownload.ContentType ?? "(无)"} {ourDownload.Body.Length} 字节");
        if (!downloadSame)
        {
            output.WriteLine($"        官方体: {Truncate(Encoding.UTF8.GetString(officialDownload.Body))}");
            output.WriteLine($"        我们体: {Truncate(Encoding.UTF8.GetString(ourDownload.Body))}");
        }

        output.WriteLine();
        output.WriteLine("  已知的刻意差异：");

        // 官方 GET data 一律 code 3（它只读请求体，GET 带不了体）；我们读查询参数因此可用。
        // 用 get_length 作探针：官方 3，我们 0 且带值——差异必须显式可见。
        const string GetProbe = "/v1/{sn}/data/?path=/MACHINE/CONTROLLER/VARIABLE@REG_X&operation=get_length";
        var officialGet = await SendAsync(officialBase, HttpMethod.Get, GetProbe.Replace("{sn}", sn), null);
        var ourGet = await SendAsync(oursBase, HttpMethod.Get, GetProbe.Replace("{sn}", sn), null);
        var differs = officialGet.Code != ourGet.Code || ShapeOf(officialGet.Body) != ShapeOf(ourGet.Body);

        output.WriteLine($"    [{(differs ? "差异" : "意外一致")}] GET /v1/{{sn}}/data/?path=...&operation=get_length");
        output.WriteLine($"        官方: HTTP {officialGet.Code} {Truncate(officialGet.Body)}（只读请求体，GET 带不了体 → 永远 3）");
        output.WriteLine($"        我们: HTTP {ourGet.Code} {Truncate(ourGet.Body)}（读查询参数，GET 可用）");
        output.WriteLine("        说明：官方 GET 形式不可用，任何 query 都是 code 3；");
        output.WriteLine("              使用官方契约的客户端不会依赖它，我们做成可用属安全超集。");
        if (!differs) failures++;

        return failures;
    }

    /// <summary>原始字节收发：文件下载成功时两边都不走 JSON，必须按字节比。</summary>
    private static async Task<(int Code, string? ContentType, byte[] Body)> SendRawAsync(
        string baseUrl, string path)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            using var resp = await http.GetAsync(baseUrl + path);
            return ((int)resp.StatusCode,
                    resp.Content.Headers.ContentType?.ToString(),
                    await resp.Content.ReadAsByteArrayAsync());
        }
        catch (Exception e) { return (0, null, Encoding.UTF8.GetBytes("EX: " + e.Message)); }
    }

    private static async Task<(int Code, string Body)> SendMultipartAsync(
        string baseUrl, string path, (string Name, string? FileName, string? Value)[] fields)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            using var form = new MultipartFormDataContent();
            foreach (var (name, fileName, value) in fields)
            {
                if (fileName is null)
                {
                    form.Add(new StringContent(value ?? string.Empty), name);
                }
                else
                {
                    var file = new ByteArrayContent(Encoding.UTF8.GetBytes(value ?? string.Empty));
                    file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
                    form.Add(file, name, fileName);
                }
            }

            using var resp = await http.PostAsync(baseUrl + path, form);
            return ((int)resp.StatusCode, await resp.Content.ReadAsStringAsync());
        }
        catch (Exception e) { return (0, "EX: " + e.Message); }
    }

    private async Task<bool> CompareOneAsync(
        TextWriter output, string label, HttpMethod method, string path, string? body)
    {
        var (officialCode, officialBody) = await SendAsync(officialBase, method, path, body);
        var (oursCode, oursBody) = await SendAsync(oursBase, method, path, body);

        // 带时间戳/内存地址的响应只比字段形状；其余逐字符比
        var same = officialCode == oursCode && ShapeOf(officialBody) == ShapeOf(oursBody);
        output.WriteLine($"    [{(same ? "一致" : "不一致")}] {label}");
        if (!same)
        {
            output.WriteLine($"        官方: HTTP {officialCode} {Truncate(officialBody)}");
            output.WriteLine($"        我们: HTTP {oursCode} {Truncate(oursBody)}");
        }
        return same;
    }

    private static async Task<(int Code, string Body)> SendAsync(
        string baseUrl, HttpMethod method, string path, string? body)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(40) };
            using var req = new HttpRequestMessage(method, baseUrl + path);
            if (body is not null)
                req.Content = new StringContent(body, Encoding.UTF8, "application/json");

            using var resp = await http.SendAsync(req);
            return ((int)resp.StatusCode, await resp.Content.ReadAsStringAsync());
        }
        catch (Exception e) { return (0, "EX: " + e.Message); }
    }

    /// <summary>
    /// 归一化响应体便于比对：
    /// <list type="bullet">
    /// <item><c>timestamp</c> 逐次不同（记录当下时间）→ 只留字段名；</item>
    /// <item>采样 <c>sub_id</c> 是随机 GUID，两边必然不同 → 只留字段名；
    ///   它是标识符不是契约值。</item>
    /// </list>
    /// <b>其余一律逐字符比，不做任何语义放宽</b>——包括 /model：
    /// 我们已用 <c>ModelReshaper</c> 复刻官方形状（补 <c>dataName</c>、对齐字段顺序），
    /// 实测与官方<b>逐字节相同</b>（11241 字符）。若将来放宽，回归就查不出来了。
    /// </summary>
    private static string ShapeOf(string body)
    {
        if (!body.StartsWith('{')) return body;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var parts = doc.RootElement.EnumerateObject()
                .OrderBy(p => p.Name, StringComparer.Ordinal)
                .Select(p => p.Name switch
                {
                    "timestamp" => "timestamp:*",
                    "value" when IsGuid(p.Value) => "value:<sub_id>",
                    _ => $"{p.Name}:{p.Value.GetRawText()}"
                });
            return string.Join(",", parts);
        }
        catch (Exception) { return body; }
    }

    /// <summary>采样订阅返回的 <c>sub_id</c> 是 GUID，比对时按标识符处理。</summary>
    private static bool IsGuid(JsonElement element)
        => element.ValueKind == JsonValueKind.String
           && Guid.TryParse(element.GetString(), out _);

    private static string Truncate(string s) => s.Length <= 170 ? s : s[..170].Replace("\n", " ") + "...";
}

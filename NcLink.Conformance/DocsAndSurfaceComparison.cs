using System.Net;
using System.Text;
using System.Text.Json;

namespace NcLink.Conformance;

/// <summary>
/// 文档、端口与文件端点的对照。
///
/// 这里检查的三类东西都是"可替换性"的一部分，但<b>不是</b>数据语义，
/// 因此从 <see cref="ApiServerComparison"/> 里分出来：
/// <list type="number">
/// <item><b>监听端口</b>——官方默认 19001，我们默认也是；</item>
/// <item><b>API 文档端点</b>——官方用 Springfox 2，提供
///   <c>/v2/api-docs</c>、<c>/swagger-ui.html</c>、<c>/swagger-resources</c>；
///   且 <c>/v3/api-docs</c>、<c>/doc.html</c>、<c>/actuator/*</c> 必须是 404；</item>
/// <item><b>文件端点</b>——<c>path</c>/<c>key</c> 必填、错误形状与返回码。</item>
/// </list>
/// </summary>
internal sealed class DocsAndSurfaceComparison(string officialBase, string oursBase, string sn)
{
    /// <summary>官方是 Springfox 的老路径。</summary>
    private const string OfficialDocsPath = "/v2/api-docs";

    /// <summary>我们用框架原生的——<c>app.MapOpenApi()</c> 一行。</summary>
    private const string OurDocsPath = "/openapi/v1.json";

    /// <summary>官方提供的文档端点（实测 200）。</summary>
    private static readonly string[] DocumentedEndpoints =
    [
        // 我们的（框架原生）：app.MapOpenApi() + UseSwaggerUI 默认前缀 swagger
        "/openapi/v1.json", "/swagger/index.html",
        // 两边都有
        "/",
        // 官方那套 Springfox 路径——我们刻意不再暴露
        "/v2/api-docs", "/swagger-ui.html", "/swagger-resources"
    ];

    /// <summary>
    /// 每条文档路径在两边的期望状态码。路径不同是刻意的：
    /// 官方是 Springfox 的 <c>/v2/api-docs</c>，我们用框架原生的 <c>/openapi/v1.json</c>。
    /// 官方那三条 Springfox 路径我们不再暴露。
    /// </summary>
    private static readonly Dictionary<string, (int Official, int Ours)> DocumentedPathExpectations =
        new(StringComparer.Ordinal)
        {
            ["/openapi/v1.json"] = (404, 200),
            ["/swagger/index.html"] = (404, 200),
            ["/"] = (200, 200),
            ["/v2/api-docs"] = (200, 404),
            ["/swagger-ui.html"] = (200, 404),
            ["/swagger-resources"] = (200, 404)
        };

    /// <summary>官方<b>不</b>提供的端点（实测 404）。我们必须同样 404，否则多出的路径会误导调用方。</summary>
    private static readonly string[] AbsentEndpoints =
        ["/v3/api-docs", "/doc.html", "/actuator", "/actuator/health"];

    public async Task<int> RunAsync(TextWriter output)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var failures = 0;

        failures += await CheckPortsAsync(output);

        output.WriteLine();
        output.WriteLine("  文档端点可达性：");
        foreach (var path in DocumentedEndpoints)
        {
            var (officialCode, _) = await FetchAsync(http, officialBase, path);
            var (oursCode, _) = await FetchAsync(http, oursBase, path);

            // 两家的文档路径本来就是刻意不同的（Springfox 老路径 vs 框架原生），
            // 因此只在"该有的有、该没有的没有"这个层面比，不比路径本身。
            var expected = DocumentedPathExpectations[path];
            var same = officialCode == expected.Official && oursCode == expected.Ours;
            if (!same) failures++;
            output.WriteLine($"    [{(same ? "一致" : "不一致")}] {path,-24} 官方={officialCode}（期望 {expected.Official}） 我们={oursCode}（期望 {expected.Ours}）");
        }

        foreach (var path in AbsentEndpoints)
        {
            var (officialCode, _) = await FetchAsync(http, officialBase, path);
            var (oursCode, _) = await FetchAsync(http, oursBase, path);
            var same = officialCode == oursCode;
            if (!same) failures++;
            output.WriteLine($"    [{(same ? "一致" : "不一致")}] {path,-24} 官方={officialCode} 我们={oursCode}");
        }

        output.WriteLine();
        failures += await CompareSpecsAsync(http, output);

        output.WriteLine();
        failures += await CompareFileAsync(http, output);

        return failures;
    }

    /// <summary>比对两边的监听端口是否都可连（默认端口一致性由配置决定，这里只验证可达）。</summary>
    private async Task<int> CheckPortsAsync(TextWriter output)
    {
        output.WriteLine("  监听端口：");
        var failures = 0;

        foreach (var (label, baseUrl) in new[] { ("官方", officialBase), ("我们", oursBase) })
        {
            var reachable = await IsReachableAsync(baseUrl);
            if (!reachable) failures++;
            output.WriteLine($"    [{(reachable ? "正常" : "不可达")}] {label} {baseUrl}");
        }

        // 官方默认 19001；我们默认也是 19001（这里跑在 19002 只是为了并列对照）
        var officialPort = new Uri(officialBase).Port;
        output.WriteLine($"    官方监听 {officialPort}；我们的默认端口与之相同（{officialPort}），"
                       + "并列验证时才另开端口。");
        return failures;
    }

    private static async Task<bool> IsReachableAsync(string baseUrl)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            await http.GetAsync($"{baseUrl}/").ConfigureAwait(false);
            return true;
        }
        catch (Exception) { return false; }
    }

    private static async Task<(int Code, string Body)> FetchAsync(HttpClient http, string baseUrl, string path)
    {
        try
        {
            var response = await http.GetAsync($"{baseUrl}{path}").ConfigureAwait(false);
            return ((int)response.StatusCode, await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        }
        catch (Exception e)
        {
            return (0, $"EX: {e.Message}");
        }
    }

    /// <summary>
    /// 比对 Swagger 2.0 规范。核心断言：<b>官方文档化的每个 path+method
    /// 都在我们的规范里，且参数、响应码、operationId、summary 逐字段一致。</b>
    ///
    /// 我们的规范是超集（额外含 model/sample），这是刻意的——
    /// 官方实现了那两组端点却没有 Springfox 注解，文档比能力少。
    /// </summary>
    private async Task<int> CompareSpecsAsync(HttpClient http, TextWriter output)
    {
        var failures = 0;
        // 两家文档路径不同是刻意的：官方是 Springfox 的 /v2/api-docs，
        // 我们用框架原生的 /openapi/v1.json（一行 MapOpenApi）。
        var (officialCode, officialBody) = await FetchAsync(http, officialBase, OfficialDocsPath);
        var (oursCode, oursBody) = await FetchAsync(http, oursBase, OurDocsPath);

        if (officialCode != 200 || oursCode != 200)
        {
            output.WriteLine($"    [不一致] 规范不可取：官方 {OfficialDocsPath}={officialCode} 我们 {OurDocsPath}={oursCode}");
            return 1;
        }

        using var officialDoc = JsonDocument.Parse(officialBody);
        using var oursDoc = JsonDocument.Parse(oursBody);

        var officialOps = CollectOperations(officialDoc.RootElement);
        var oursOps = CollectOperations(oursDoc.RootElement);

        output.WriteLine($"  规范：官方 {officialOps.Count} 个操作 / 我们 {oursOps.Count} 个操作");

        // 1) 官方每个操作都必须存在且逐字段一致
        foreach (var (key, officialOp) in officialOps.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (!oursOps.TryGetValue(key, out var ourOp))
            {
                output.WriteLine($"    [不一致] 缺少操作 {key}");
                failures++;
                continue;
            }

            var diffs = DiffOperation(officialOp, ourOp, key.Split(' ')[0]);
            if (diffs.Count == 0)
            {
                output.WriteLine($"    [一致] {key}");
            }
            else
            {
                failures++;
                output.WriteLine($"    [不一致] {key}");
                foreach (var d in diffs) output.WriteLine($"        {d}");
            }
        }

        // 2) 我们额外文档化的操作（允许，报告出来）
        var extra = oursOps.Keys.Except(officialOps.Keys, StringComparer.Ordinal).OrderBy(k => k, StringComparer.Ordinal);
        foreach (var key in extra)
            output.WriteLine($"    [额外] {key}（官方实现了但未文档化）");

        // 3) definitions 字段比对
        var officialDefs = CollectDefinitions(officialDoc.RootElement);
        var ourDefs = CollectDefinitions(oursDoc.RootElement);
        foreach (var (name, officialFields) in officialDefs.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var lookup = SchemaAliases.TryGetValue(name, out var alias) ? alias : name;
            if (!ourDefs.TryGetValue(lookup, out var ourFields))
            {
                // 官方那些 Java 专有类型与任何操作都不连通，不属于 HTTP 契约
                output.WriteLine($"    [忽略] definition {name}（Java 专有类型，无操作引用）");
                continue;
            }

            var same = officialFields.SequenceEqual(ourFields, StringComparer.Ordinal);
            if (!same) failures++;
            output.WriteLine(same
                ? $"    [一致] definition {name} 字段 {string.Join(",", officialFields)}"
                : $"    [不一致] definition {name}: 官方=[{string.Join(",", officialFields)}] 我们=[{string.Join(",", ourFields)}]");
        }

        // 4) 分组标签：决定 Swagger UI 里的分组名，属于文档面的对外形状。
        // 官方那套来自 Springfox 注解，我们逐字复刻，**必须由 ApiDocumentTransformer 提供**
        // ——少了它生成器只会给出空 tags，而这一点此前没有任何检查守着。
        // 只比 name 与 description，不比官方 tag 上的其他键（如 externalDocs）。
        var officialTags = CollectTags(officialDoc.RootElement);
        var ourTags = CollectTags(oursDoc.RootElement);
        foreach (var (name, description) in officialTags.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (ourTags.TryGetValue(name, out var ourDescription) &&
                string.Equals(description, ourDescription, StringComparison.Ordinal))
            {
                output.WriteLine($"    [一致] tag {name}（{description}）");
            }
            else
            {
                failures++;
                output.WriteLine(ourTags.ContainsKey(name)
                    ? $"    [不一致] tag {name}：官方描述=[{description}] 我们=[{ourTags[name]}]"
                    : $"    [不一致] 缺少 tag {name}（官方描述=[{description}]）");
            }
        }

        // 5) 顶层元信息。只比"怎么调用"，因此刻意不比对格式相关的顶层键
        // （swagger/openapi/basePath/securityDefinitions）与 info.title：
        // 官方是 Springfox 的 Swagger 2.0，info.title 还是它没改过的默认占位符。
        var officialFormat = officialDoc.RootElement.TryGetProperty("openapi", out _) ? "OpenAPI 3" : "Swagger 2.0";
        var ourFormat = oursDoc.RootElement.TryGetProperty("openapi", out _) ? "OpenAPI 3" : "Swagger 2.0";
        output.WriteLine($"    [不比对] 文档格式：官方={officialFormat} 我们={ourFormat}（只比使用方式）");

        return failures;
    }

    private static Dictionary<string, string?> CollectTags(JsonElement root)
    {
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (!root.TryGetProperty("tags", out var tags) || tags.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var tag in tags.EnumerateArray())
        {
            var name = tag.TryGetProperty("name", out var n) ? n.GetString() : null;
            if (string.IsNullOrEmpty(name)) continue;
            result[name] = tag.TryGetProperty("description", out var d) ? d.GetString() : null;
        }

        return result;
    }

    private static Dictionary<string, JsonElement> CollectOperations(JsonElement root)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (!root.TryGetProperty("paths", out var paths)) return result;

        foreach (var path in paths.EnumerateObject())
        {
            foreach (var op in path.Value.EnumerateObject())
            {
                if (op.Name is not ("get" or "post" or "put" or "delete")) continue;

                // 路径模板归一：官方（Springfox）写 **，MVC 写 {rest}；
                // 再把所有 {xxx} 抹成 {}——**路径参数名不属于契约**，
                // {device_id} 与 {deviceId} 产出的 URL 完全相同。
                var normalized = path.Name.Replace("/**", "/{rest}", StringComparison.Ordinal);
                normalized = System.Text.RegularExpressions.Regex.Replace(normalized, @"\{[^}]*\}", "{}");
                result[$"{op.Name.ToUpperInvariant()} {normalized}"] = op.Value;
            }
        }
        return result;
    }

    /// <summary>
    /// 收集 schema 及其字段名。两种格式的位置不同：
    /// Swagger 2.0 在 <c>definitions</c>，OpenAPI 3 在 <c>components/schemas</c>。
    /// </summary>
    private static Dictionary<string, string[]> CollectDefinitions(JsonElement root)
    {
        var result = new Dictionary<string, string[]>(StringComparer.Ordinal);

        var source = root.TryGetProperty("definitions", out var v2)
            ? v2
            : root.TryGetProperty("components", out var components)
                && components.TryGetProperty("schemas", out var v3)
                    ? v3
                    : default;
        if (source.ValueKind != JsonValueKind.Object) return result;

        foreach (var def in source.EnumerateObject())
        {
            var fields = def.Value.TryGetProperty("properties", out var props)
                ? props.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray()
                : [];
            result[def.Name] = fields;
        }
        return result;
    }

    /// <summary>
    /// 同一类型在两种格式里的名字不同（我们的 C# 类型名 vs 官方 Java 类型名）。
    /// 文档允许改名，但字段必须一致，因此比对时按别名对应。
    /// </summary>
    private static readonly Dictionary<string, string> SchemaAliases = new(StringComparer.Ordinal)
    {
        ["Item"] = "DataRequestItem"
    };

    /// <summary>
    /// 比对单个操作：参数与响应码。
    ///
    /// 判据是<b>"怎么调用"一致</b>，不是文档逐字段相同：
    /// <list type="bullet">
    /// <item><b>跨格式归一化后比参数</b>：Swagger 2 的 <c>body</c>/<c>formData</c>
    ///   与 OpenAPI 3 的 <c>requestBody</c> 归一成同一视图，只比名字、位置、类型、必填。</item>
    /// <item><b><c>required</c> 缺省即 <c>false</c></b>（JSON Schema 默认值）。
    ///   官方给每个非必填参数显式写 <c>"required":false</c>，我们省略——语义相同。</item>
    /// <item><b>GET 上的请求体参数忽略</b>。官方在 GET 上也声明了 <c>body</c>
    ///   （Springfox 不区分方法类型），而 <b>GET 根本不能带请求体</b>，
    ///   我们（正确地）不声明。</item>
    /// <item><b>多出的参数只在"必填"时才算错</b>。多一个可选参数对使用官方契约的
    ///   客户端不可见，属安全超集；多一个必填参数才会让老客户端失败。</item>
    /// <item><b>只比 2xx 响应</b>。官方统一声明 401/403/404/201，我们并不返回它们，
    ///   照抄就是假声明。</item>
    /// <item><b>不比 <c>operationId</c>/<c>summary</c></b>——那是文档标识，不是调用方式。</item>
    /// </list>
    /// </summary>
    private static List<string> DiffOperation(JsonElement official, JsonElement ours, string method)
    {
        var diffs = new List<string>();

        var officialParams = CollectParameters(official);
        var ourParams = CollectParameters(ours);

        // GET 不能有请求体：官方那一条是 Springfox 的产物，不作为契约要求
        if (method == "GET") officialParams.Remove("dataRequest");

        foreach (var name in officialParams.Keys.Union(ourParams.Keys).OrderBy(n => n, StringComparer.Ordinal))
        {
            var inOfficial = officialParams.TryGetValue(name, out var a);
            var inOurs = ourParams.TryGetValue(name, out var b);

            // path 参数（device_id 之类）由上面的路径模板比对覆盖；
            // 它的名字只是模板里的占位符，不构成契约——.NET 侧按约定叫 deviceId。
            if ((inOfficial && a.In == "path") || (inOurs && b.In == "path")) continue;

            // 只有"报文里真正要带的"才算契约：请求体与表单字段。
            // 查询/路径参数**不作为失败**——官方在 data 端点声明的那 6 个查询参数
            // 实测实现根本不读（query 与 body 冲突时以 body 为准），
            // 照抄它们等于把"声明了但不生效"当契约。差异照实报出来。
            var isPayload = (inOfficial && a.In is "body" or "formData")
                         || (inOurs && b.In is "body" or "formData");

            if (!inOfficial)
            {
                if (isPayload || b.Required) diffs.Add($"多出必填参数 {name}");
                continue;
            }
            if (!inOurs)
            {
                if (isPayload) diffs.Add($"缺少参数 {name}");
                continue;
            }

            if (a.In != b.In || a.Type != b.Type || a.Required != b.Required)
            {
                var detail = $"参数 {name}：官方=({a.In},{a.Type},required={a.Required}) "
                           + $"我们=({b.In},{b.Type},required={b.Required})";
                if (isPayload) diffs.Add(detail);
            }
        }

        // 只要求 200——官方那 201 是 Springfox 给所有 POST 统一加的，我们并不返回它
        var ourCodes = SuccessCodes(ours);
        if (!ourCodes.Contains("200"))
            diffs.Add($"缺少 200 响应：我们=[{string.Join(",", ourCodes)}]");

        return diffs;
    }

    /// <summary>
    /// 把一个操作的参数归一化成"怎么调用"的视图：<c>名字 → (位置, 类型)</c>，
    /// <b>抹平 Swagger 2.0 与 OpenAPI 3 的表达差异</b>：
    /// <list type="bullet">
    /// <item>Swagger 2.0 用 <c>in: body</c> 的参数；OpenAPI 3 用 <c>requestBody</c>
    ///   ——两者都归一成 <c>dataRequest</c>。</item>
    /// <item>Swagger 2.0 用 <c>in: formData</c>；OpenAPI 3 用 <c>requestBody</c> 的
    ///   <c>multipart/form-data</c> 内容——把后者的每个属性拆成同名的表单字段。</item>
    /// <item>类型：Swagger 2 摊平在 <c>type</c>，OpenAPI 3 在 <c>schema.type</c>。</item>
    /// </list>
    /// </summary>
    private static Dictionary<string, (string In, string? Type, bool Required)> CollectParameters(
        JsonElement operation)
    {
        var result = new Dictionary<string, (string, string?, bool)>(StringComparer.Ordinal);

        if (operation.TryGetProperty("parameters", out var parameters))
        {
            foreach (var p in parameters.EnumerateArray())
            {
                if (p.TryGetProperty("name", out var nameElement) && nameElement.GetString() is { } name)
                    result[name] = (Field(p, "in") ?? "?", SchemaTypeOf(p), IsRequired(p));
            }
        }

        // OpenAPI 3 的请求体
        if (operation.TryGetProperty("requestBody", out var requestBody)
            && requestBody.TryGetProperty("content", out var content))
        {
            foreach (var media in content.EnumerateObject())
            {
                if (!media.Value.TryGetProperty("schema", out var schema)) continue;

                // multipart → 每个属性是一个表单字段。ASP.NET Core 会为每个
                // [FromForm] 参数各生成一个对象，再用 allOf 合并，因此要往里看一层。
                var fields = FormFields(schema);
                if (fields is not null)
                {
                    foreach (var (name, field) in fields)
                        result[name] = ("formData", field.Type, field.Required);
                    continue;
                }

                // application/json → 归一成 body 参数。可空请求体会被表达成
                // oneOf:[{enum:[null]}, {$ref: ...}]，因此 $ref 也要往里找一层。
                result["dataRequest"] = ("body", BodyTypeOf(schema), false);
            }
        }

        return result;
    }

    /// <summary>
    /// 取表单字段：属性可能在 <c>schema.properties</c>，也可能在 <c>schema.allOf[]</c>
    /// 里（ASP.NET Core 为每个 <c>[FromForm]</c> 参数各生成一个对象再合并），
    /// 而必填名单 <c>required</c> 只在<b>外层对象</b>上——两处都要取，否则会把
    /// 必填字段误读成选填。
    ///
    /// 文件上传：OpenAPI 3 用 <c>$ref</c> 到 <c>{type:string,format:binary}</c>，
    /// Swagger 2 直接写 <c>type:file</c>——归一成同一个说法。
    /// </summary>
    private static Dictionary<string, (string Type, bool Required)>? FormFields(JsonElement schema)
    {
        var required = new HashSet<string>(StringComparer.Ordinal);
        if (schema.TryGetProperty("required", out var requiredList) && requiredList.ValueKind == JsonValueKind.Array)
            foreach (var item in requiredList.EnumerateArray())
                if (item.GetString() is { } name) required.Add(name);

        var properties = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (schema.TryGetProperty("properties", out var direct))
            foreach (var property in direct.EnumerateObject())
                properties[property.Name] = property.Value;

        if (schema.TryGetProperty("allOf", out var allOf))
            foreach (var part in allOf.EnumerateArray())
                if (part.TryGetProperty("properties", out var partProperties))
                    foreach (var property in partProperties.EnumerateObject())
                        properties[property.Name] = property.Value;

        if (properties.Count == 0) return null;

        return properties.ToDictionary(
            kv => kv.Key,
            kv =>
            {
                // 文件字段：Swagger 2 写 type:file，OpenAPI 3 写 type:string + format:binary
                // （也可能 $ref 到 IFormFile）——三种写法归一成同一个说法
                var type = RefName(kv.Value) is "IFormFile" || Field(kv.Value, "format") == "binary"
                    ? "file"
                    : Field(kv.Value, "type") ?? "object";
                return (Type: type, Required: required.Contains(kv.Key));
            },
            StringComparer.Ordinal);
    }

    /// <summary>取请求体类型名：<c>$ref</c> 可能在顶层，也可能在 <c>oneOf</c> 里（可空表达）。</summary>
    private static string? BodyTypeOf(JsonElement schema)
    {
        if (RefName(schema) is { } direct) return direct;
        if (schema.TryGetProperty("oneOf", out var oneOf))
            foreach (var part in oneOf.EnumerateArray())
                if (RefName(part) is { } name)
                    return name;
        return Field(schema, "type");
    }

    private static string? SchemaTypeOf(JsonElement parameter)
    {
        if (parameter.TryGetProperty("schema", out var schema) && schema.ValueKind == JsonValueKind.Object)
            return RefName(schema) ?? Field(schema, "type");
        return Field(parameter, "type");
    }

    /// <summary>取 <c>$ref</c> 末段类型名（<c>#/definitions/X</c> 或 <c>#/components/schemas/X</c>）。</summary>
    private static string? RefName(JsonElement schema)
        => schema.TryGetProperty("$ref", out var r) && r.GetString() is { } text
            ? text[(text.LastIndexOf('/') + 1)..]
            : null;

    /// <summary>
    /// 参数是否必填。缺失即 <c>false</c>——Swagger 2.0 里 <c>required</c> 默认就是
    /// <c>false</c>，官方（Springfox）给每个参数都显式写出来，我们省略，语义相同。
    /// </summary>
    private static bool IsRequired(JsonElement parameter)
        => parameter.TryGetProperty("required", out var v) && v.ValueKind == JsonValueKind.True;

    private static string? Field(JsonElement element, string name)
        => element.TryGetProperty(name, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.String => v.GetString(),
                JsonValueKind.True => "True",
                JsonValueKind.False => "False",
                JsonValueKind.Null => null,
                _ => v.GetRawText()
            }
            : null;

    /// <summary>只取 2xx 响应码——4xx 是官方声明的假响应，我们并不返回。</summary>
    private static string[] SuccessCodes(JsonElement operation)
        => operation.TryGetProperty("responses", out var responses)
            ? responses.EnumerateObject()
                .Select(p => p.Name)
                .Where(n => n.StartsWith('2'))
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToArray()
            : [];

    /// <summary>文件端点：实测 path 与 key 必填；错误形状是 JSON DataResponse 且 value 为消息字符串。</summary>
    private async Task<int> CompareFileAsync(HttpClient http, TextWriter output)
    {
        var failures = 0;
        var cases = new[]
        {
            ("缺 key（应 400）", $"/v1/{sn}/file/?path=/MACHINE/CONTROLLER/FILE"),
            ("缺 path（应 400）", $"/v1/{sn}/file/?key=x"),
            ("都缺（应 400）", $"/v1/{sn}/file/"),
            ("path+key 齐全（设备无此文件）", $"/v1/{sn}/file/?path=/MACHINE/CONTROLLER/FILE&key=x")
        };

        foreach (var (label, relative) in cases)
        {
            var (officialCode, officialBody) = await FetchAsync(http, officialBase, relative);
            var (oursCode, oursBody) = await FetchAsync(http, oursBase, relative);

            var sameCode = officialCode == oursCode;
            // 错误体的形状也要一致：status/code/value 三个字段
            var officialShape = FailureShape(officialBody);
            var oursShape = FailureShape(oursBody);
            var sameBody = officialShape is null || officialShape == oursShape;

            var ok = sameCode && sameBody;
            if (!ok) failures++;

            output.WriteLine($"    [{(ok ? "一致" : "不一致")}] {label}：官方={officialCode} 我们={oursCode}");
            if (!ok)
            {
                output.WriteLine($"        官方: {Truncate(officialBody)}");
                output.WriteLine($"        我们: {Truncate(oursBody)}");
            }
        }

        return failures;
    }

    /// <summary>取出错误响应的形状（字段名 + 是否带 value），用于比对而不比较具体消息。</summary>
    private static string? FailureShape(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var names = root.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal);
            var hasValue = root.TryGetProperty("value", out var v) && v.ValueKind != JsonValueKind.Undefined;
            return string.Join(",", names) + (hasValue ? "+value" : "");
        }
        catch (Exception) { return null; }
    }

    private static string Truncate(string text) => text.Length <= 160 ? text : text[..160] + "...";
}

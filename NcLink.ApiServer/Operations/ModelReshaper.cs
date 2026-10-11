using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NcLink.ApiServer.Operations;

/// <summary>
/// 把设备发来的模型原文整理成<b>官方 apiServer 的形状</b>。
///
/// 官方把模型解析成 Java POJO 再序列化，因此输出有两个可观测差异（均实测：
/// 见 <c>docs/api-server.md</c>）：
/// <list type="number">
/// <item><b>补出 <c>dataName</c></b>。设备原文里没有这个字段；
///   官方对<b>数据节点</b>填的是该节点的 <c>number</c>（实测 43 个非空，
///   正好等于模型里 <c>VARIABLE</c> 的数量，取值就是 <c>REG_X</c> / <c>EVENT</c>
///   这类 <c>number</c>），结构节点则为 <c>null</c>。
///   <b>这不是格式差异而是内容</b>——不补就等于把变量名丢了。</item>
/// <item><b>字段顺序</b>跟着 Java 类声明走，与设备原文不同。</item>
/// </list>
///
/// 判据：<b>带子节点（<c>devices</c>/<c>configs</c>/<c>dataItems</c>/<c>components</c>）
/// 的是结构节点</b>，用结构顺序且不加 <c>dataName</c>；其余是数据节点。
/// </summary>
internal static class ModelReshaper
{
    /// <summary>
    /// 结构节点的字段顺序（实测）。
    ///
    /// 用拓扑排序从全部 21 个结构节点推出<b>偏序</b>：
    /// <c>name → id → type → description → {devices, number} → configs → dataItems → components → version</c>。
    /// <c>devices</c>（仅 <c>NC_LINK_ROOT</c>）与 <c>number</c>（仅 <c>AXIS</c>）
    /// 从不同时出现，两者之间无约束，任何相对顺序都能得到逐字节相同的输出。
    /// </summary>
    private static readonly string[] StructuralOrder =
        ["name", "id", "type", "description", "devices", "number", "configs", "dataItems", "components", "version"];

    /// <summary>
    /// 数据节点的字段顺序（实测，同样由拓扑排序推出偏序）。
    ///
    /// 约束：<c>dataName → name → id → type</c>；
    /// <c>settable → number → dataType</c>（来自 <c>VARIABLE</c> 的
    /// <c>settable, number, dataType</c>）；<c>sampleInterval → ids → uploadInterval</c>；
    /// <c>value</c> 独立。三组互不共现，组间顺序可任选。
    /// </summary>
    private static readonly string[] DataOrder =
        ["dataName", "name", "id", "type", "settable", "number", "dataType", "value", "sampleInterval", "ids", "uploadInterval"];

    private static readonly string[] ChildKeys = ["devices", "configs", "dataItems", "components"];

    /// <summary>
    /// 输出时<b>不转义非 ASCII</b>：官方的 JSON 里中文是原样的
    /// （<c>"name":"机床模型文件"</c>），默认编码器会写成 <c>\uXXXX</c> 而与之不同。
    /// </summary>
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>把模型原文整理成官方形状；解析失败时返回 null。</summary>
    public static JsonNode? Reshape(string rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson)) return null;

        var node = JsonNode.Parse(rawJson);
        return node is null ? null : Visit(node);
    }

    private static JsonNode? Visit(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                var isStructural = ChildKeys.Any(k => obj.ContainsKey(k));
                var order = isStructural ? StructuralOrder : DataOrder;
                var result = new JsonObject();

                foreach (var key in order)
                {
                    if (key == "dataName")
                    {
                        // 数据节点的 dataName 取 number；没有 number 就是 null
                        obj.TryGetPropertyValue("number", out var number);
                        result["dataName"] = number is null ? null : JsonValue.Create(number.GetValue<string>());
                        continue;
                    }

                    if (obj.TryGetPropertyValue(key, out var value))
                        result[key] = ChildKeys.Contains(key) ? Visit(value) : value?.DeepClone();
                }

                // 顺序表之外的字段（未来设备新增的）原样保留，不丢信息
                foreach (var (key, value) in obj)
                    if (!result.ContainsKey(key))
                        result[key] = ChildKeys.Contains(key) ? Visit(value) : value?.DeepClone();

                return result;

            case JsonArray array:
                var list = new JsonArray();
                foreach (var item in array) list.Add(Visit(item));
                return list;

            default:
                return node?.DeepClone();
        }
    }

    /// <summary>序列化为字符串（中文不转义，与官方一致）。</summary>
    public static string ToJson(JsonNode node) => node.ToJsonString(WriteOptions);
}

using System.Reflection;
using NcLink.Client.Devices.Parsers;

namespace NcLink.Client.Devices.Models;

/// <summary>
/// 内置示例设备模型的读取入口（只提供 JSON 文本）。
///
/// 仅供<b>离线开发与测试</b>使用。真实项目里模型应从设备的探测接口取回
/// （<c>INcLinkOperations.ProbeAsync</c>），不要依赖这个内置文件。
///
/// 解析请用 <c>NcLink.Protocol.SampleDeviceModel.Parse(...)</c>。
/// </summary>
public static class EmbeddedSampleModel
{
    /// <summary>
    /// 资源文件名。资源全名由「根命名空间 + 文件路径」组成，
    /// 因此这里按<b>文件名后缀</b>匹配，避免移动文件或改命名空间后失配。
    /// </summary>
    private const string ResourceFileName = "sample-model.json";

    private static readonly Lazy<string> LazyJson = new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>示例模型 JSON 文本。</summary>
    public static string Json => LazyJson.Value;

    private static string Load()
    {
        var assembly = typeof(EmbeddedSampleModel).Assembly;
        var names = assembly.GetManifestResourceNames();

        var resourceName = names.FirstOrDefault(
                               n => n.EndsWith("." + ResourceFileName, StringComparison.Ordinal))
                           ?? throw new InvalidOperationException(
                               $"找不到内置模型资源（*{ResourceFileName}）。现有资源：{string.Join(", ", names)}");

        using var stream = assembly.GetManifestResourceStream(resourceName)
                           ?? throw new InvalidOperationException($"无法打开内置模型资源：{resourceName}");

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

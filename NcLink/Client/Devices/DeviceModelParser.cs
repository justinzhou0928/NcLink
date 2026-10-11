using NcLink.Client.Devices;
using System.Text.Json;
using NcLink.Protocol.Engine;

namespace NcLink.Client.Devices;

/// <summary>
/// 设备模型解析器：把 model.json 文本解析为 <see cref="DeviceModel"/>。
/// </summary>
public static class DeviceModelParser
{
    /// <summary>
    /// 解析模型文件。
    /// </summary>
    /// <param name="json">model.json 内容。</param>
    /// <param name="deviceId">模型所属设备的 SN。</param>
    /// <exception cref="ArgumentException">内容为空。</exception>
    /// <exception cref="JsonException">内容不是合法的模型 JSON。</exception>
    public static DeviceModel Parse(string json, string deviceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);

        var raw = JsonSerializer.Deserialize<DeviceModelRaw>(json, NcLinkJson.Options)
                  ?? throw new JsonException("模型文件内容为空。");

        // 原文一并带上：让"原样返回模型"的调用方（如 ApiServer 的 GET /model）
        // 拿到与设备发出的一致的内容，而不是重新序列化后的近似结果
        return DeviceModel.Build(deviceId, raw, json);
    }

    /// <summary>尝试解析；失败时返回 null 并给出原因。</summary>
    public static bool TryParse(string json, string deviceId, out DeviceModel? model, out string? error)
    {
        try
        {
            model = Parse(json, deviceId);
            error = null;
            return true;
        }
        catch (Exception e) when (e is JsonException or ArgumentException)
        {
            model = null;
            error = e.Message;
            return false;
        }
    }
}

/// <summary>
/// 内置示例模型的解析入口：把"读取内置 JSON"与"解析"串起来，供离线开发与测试使用。
/// </summary>
public static class SampleDeviceModel
{
    /// <summary>解析内置示例模型。</summary>
    public static DeviceModel Parse(string deviceId = "SAMPLE_DEVICE")
        => DeviceModelParser.Parse(EmbeddedSampleModel.Json, deviceId);
}

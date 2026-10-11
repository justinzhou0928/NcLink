using NcLink.Client.Devices.Engine;
namespace NcLink.Client.Devices.Interfaces;

/// <summary>
/// 探测能力抽象：向设备请求模型 JSON 文本。
///
/// <c>DeviceModelCache</c> 只依赖这个契约，因此缓存层不必知道客户端公开 API 长什么样。
/// </summary>
internal interface IDeviceProbe
{
    Task<string> ProbeModelJsonAsync(
        string deviceId, TimeSpan? timeout, CancellationToken cancellationToken);
}

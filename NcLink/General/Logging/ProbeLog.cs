using Microsoft.Extensions.Logging;

namespace NcLink.General.Logging;

/// <summary>设备探测的日志事件。</summary>
internal static partial class ProbeLog
{
    [LoggerMessage(EventId = 3001, Level = LogLevel.Information,
        Message = "探测设备 {DeviceId} 成功，模型 JSON {Length} 字符")]
    internal static partial void LogProbed(ILogger logger, string deviceId, int length);
}

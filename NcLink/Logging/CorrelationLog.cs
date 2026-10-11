using Microsoft.Extensions.Logging;

namespace NcLink.Logging;

/// <summary>
/// 请求-响应关联的日志事件。
///
/// 本项目把所有 <c>[LoggerMessage]</c> 声明集中在这个文件夹里
/// （对应各组件一个文件），而不是散落在各组件的嵌套类中——
/// 排查问题时"日志在哪"的答案是唯一的。
/// </summary>
internal static partial class CorrelationLog
{
    [LoggerMessage(EventId = 1001, Level = LogLevel.Debug,
        Message = "发送请求：设备 {DeviceId} 主题 {Topic} 关联 {CorrelationId} 超时 {Timeout}")]
    internal static partial void LogRequestSent(
        ILogger logger, string? deviceId, string topic, string correlationId, TimeSpan timeout);

    [LoggerMessage(EventId = 1002, Level = LogLevel.Debug,
        Message = "响应已关联：主题 {Topic} 关联 {CorrelationId} 方式 {MatchKind}")]
    internal static partial void LogResponseMatched(
        ILogger logger, string topic, string correlationId, string matchKind);

    [LoggerMessage(EventId = 1003, Level = LogLevel.Warning,
        Message = "请求超时：设备 {DeviceId} 主题 {Topic} 超时 {Timeout} 在途请求 {PendingCount}")]
    internal static partial void LogRequestTimedOut(
        ILogger logger, string? deviceId, string topic, TimeSpan timeout, int pendingCount);

    [LoggerMessage(EventId = 1004, Level = LogLevel.Warning,
        Message = "设备从未出现（SN 错误或未接入）：设备 {DeviceId} 主题 {Topic}")]
    internal static partial void LogDeviceNeverSeen(ILogger logger, string? deviceId, string topic);

    [LoggerMessage(EventId = 1005, Level = LogLevel.Debug,
        Message = "收到无人认领的响应：主题 {Topic} 原因 {Reason}")]
    internal static partial void LogUnmatchedResponse(ILogger logger, string topic, string reason);
}

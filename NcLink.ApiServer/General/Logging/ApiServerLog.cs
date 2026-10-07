using Microsoft.Extensions.Logging;

namespace NcLink.ApiServer.General.Logging;

/// <summary>
/// ApiServer 的日志事件。
///
/// 与核心库同样的约定：日志集中在 <c>General/Logging/</c>，按组件分文件，
/// 方法名 <c>Log&lt;动作&gt;</c>，EventId 按组件分段（采样缓存用 6xxx）。
/// 不要在各组件内部写嵌套的 <c>Log</c> 类。
/// </summary>
internal static partial class ApiServerLog
{
    [LoggerMessage(EventId = 6001, Level = LogLevel.Information,
        Message = "建立采样订阅 {SubId}：{DeviceId}/{ChannelId}")]
    internal static partial void LogSubscribed(ILogger logger, string subId, string deviceId, string channelId);

    [LoggerMessage(EventId = 6002, Level = LogLevel.Debug,
        Message = "取走采样 {SubId}：{Count} 帧")]
    internal static partial void LogDrained(ILogger logger, string subId, int count);

    [LoggerMessage(EventId = 6003, Level = LogLevel.Information,
        Message = "取消采样订阅 {SubId}")]
    internal static partial void LogUnsubscribed(ILogger logger, string subId);

    [LoggerMessage(EventId = 6004, Level = LogLevel.Information,
        Message = "回收空闲采样订阅 {SubId}（{DeviceId}/{ChannelId}）")]
    internal static partial void LogReaped(ILogger logger, string subId, string deviceId, string channelId);

    [LoggerMessage(EventId = 6005, Level = LogLevel.Warning,
        Message = "采样缓存回收循环异常退出")]
    internal static partial void LogReaperFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 6101, Level = LogLevel.Warning,
        Message = "数据接口请求不合法：{Reason}")]
    internal static partial void LogBadDataRequest(ILogger logger, string reason);

    [LoggerMessage(EventId = 6102, Level = LogLevel.Warning,
        Message = "设备 {DeviceId} 操作 {Operation} 失败：{Reason}")]
    internal static partial void LogOperationFailed(ILogger logger, string deviceId, string operation, string reason);

    [LoggerMessage(EventId = 6201, Level = LogLevel.Information,
        Message = "ApiServer 启动，监听 {Urls}，Broker {Broker}")]
    internal static partial void LogStarted(ILogger logger, string urls, string broker);

    [LoggerMessage(EventId = 6301, Level = LogLevel.Debug,
        Message = "v2 数据接口 {Method} 设备 {DeviceId} → code {Code}（复刻官方 v2 的退化行为）")]
    internal static partial void LogV2Call(ILogger logger, string method, string deviceId, int code);
}

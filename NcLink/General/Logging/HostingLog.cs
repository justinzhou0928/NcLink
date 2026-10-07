using Microsoft.Extensions.Logging;
using NcLink.Protocol.Contracts;

namespace NcLink.General.Logging;

/// <summary>宿主集成（<c>NcLinkHostedService</c>）的日志事件。</summary>
internal static partial class HostingLog
{
    [LoggerMessage(EventId = 5001, Level = LogLevel.Information,
        Message = "NcLink 已随宿主启动，连接状态 {State}")]
    internal static partial void LogStarted(ILogger logger, NcLinkConnectionState state);

    [LoggerMessage(EventId = 5002, Level = LogLevel.Warning,
        Message = "NcLink 启动时连接失败，将由重连循环继续尝试")]
    internal static partial void LogStartFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 5003, Level = LogLevel.Information,
        Message = "NcLink 已随宿主停止，连接与订阅已释放")]
    internal static partial void LogStopped(ILogger logger);
}

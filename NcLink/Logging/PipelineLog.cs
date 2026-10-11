using Microsoft.Extensions.Logging;
using NcLink.Protocol.Models;

namespace NcLink.Logging;

/// <summary>连接与请求管线的日志事件。</summary>
internal static partial class PipelineLog
{
    [LoggerMessage(EventId = 2001, Level = LogLevel.Information,
        Message = "正在连接 MQTT Broker：{Endpoint}")]
    internal static partial void LogConnecting(ILogger logger, string endpoint);

    [LoggerMessage(EventId = 2002, Level = LogLevel.Information,
        Message = "已连接 MQTT Broker：{Endpoint}")]
    internal static partial void LogConnected(ILogger logger, string endpoint);

    [LoggerMessage(EventId = 2003, Level = LogLevel.Debug,
        Message = "已订阅全部响应主题，共 {Count} 个通配符")]
    internal static partial void LogSubscribedAllResponseTopics(ILogger logger, int count);
}

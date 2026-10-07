using Microsoft.Extensions.Logging;

namespace NcLink.General.Logging;

/// <summary>采样分发的日志事件。</summary>
internal static partial class SamplingLog
{
    /// <summary>
    /// 采样主题形状不对（应为 <c>Sample/{deviceId}/{channelId}</c>）。
    ///
    /// 这条替代了原先的"通道 id 歧义则丢帧"日志——那个机制基于错误前提
    /// （以为主题不含 SN），已随实测结果移除。
    /// </summary>
    [LoggerMessage(EventId = 4001, Level = LogLevel.Warning,
        Message = "丢弃采样帧：主题 {Topic} 不是 Sample/{{deviceId}}/{{channelId}} 三层形状")]
    internal static partial void LogMalformedSampleTopic(ILogger logger, string topic);
}

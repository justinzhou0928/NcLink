using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NcLink.Client;
using NcLink.Logging;

namespace NcLink;

/// <summary>
/// 把 <see cref="NcLinkClient"/> 接入 .NET 宿主：注册后随宿主启动而连接、随宿主停止而释放。
///
/// 用法（在 <c>AddNcLink</c> 之后）：
/// <code>
/// services.AddNcLink(options, transportFactory);
/// services.AddNcLinkHostedService();   // 或 services.AddHostedService&lt;NcLinkHostedService&gt;()
/// </code>
///
/// <b>连接失败不会阻断宿主启动</b>：记录警告后继续，由传输层的重连循环负责恢复
/// （见 <c>NcLink.Transport.Mqtt.MqttTransport</c> 的指数退避重连）。
/// 这样 EMQX 启动稍慢或网络抖动不会导致整个应用起不来。
/// </summary>
public sealed class NcLinkHostedService : IHostedService
{
    private readonly NcLinkClient _client;
    private readonly ILogger<NcLinkHostedService> _logger;

    public NcLinkHostedService(NcLinkClient client, ILogger<NcLinkHostedService> logger)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _client.InitializeAsync(cancellationToken).ConfigureAwait(false);
            HostingLog.LogStarted(_logger, _client.ConnectionState);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // 不阻断宿主启动：传输层的重连循环会继续尝试。
            // 若这里直接抛出，EMQX 晚启动几秒就会让整个应用起不来。
            // 注意取消异常不在此列——那是调用方意图，应当向上传递。
            HostingLog.LogStartFailed(_logger, e);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // 释放传输连接与全部采样订阅
        await _client.DisposeAsync().ConfigureAwait(false);
        HostingLog.LogStopped(_logger);
    }
}

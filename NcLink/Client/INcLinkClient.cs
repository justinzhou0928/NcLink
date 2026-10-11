using NcLink.Client.Connection;
using NcLink.Client.Files;
using NcLink.Client.Query;
using NcLink.Client.Set;
using NcLink.Protocol;

namespace NcLink.Client;

/// <summary>
/// NC-Link 客户端门面契约：把官方功能面（查询 / 设置 / 文件）与连接管理组合起来。
///
/// 需要更小依赖面时，直接注入能力接口：
/// <list type="bullet">
/// <item><see cref="IQuery"/> —— 官方 5.1 查询 + 探测 + 采样</item>
/// <item><see cref="ISet"/> —— 官方 5.1 设置</item>
/// <item><see cref="IFileTransfer"/> —— 官方 5.2 获取/下发文件</item>
/// </list>
/// </summary>
public interface INcLinkClient : IQuery, ISet, IDisposable, IAsyncDisposable
{
    /// <summary>连接状态。</summary>
    NcLinkConnectionState ConnectionState { get; }

    /// <summary>连接状态变化。</summary>
    event Action<NcLinkConnectionState>? ConnectionStateChanged;

    /// <summary>G 代码文件上下行（官方 5.2）。</summary>
    IFileTransfer Files { get; }

    /// <summary>建立与 Broker 的连接（幂等）。</summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);
}

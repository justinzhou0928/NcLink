using Microsoft.Extensions.Logging;
using NcLink.Client.Connection;
using NcLink.Client.Files;
using NcLink.Client;
using NcLink.Client.Query;
using NcLink.Client.Set;
using NcLink.Protocol;

namespace NcLink.Client;

/// <summary>
/// NC-Link 客户端（门面）。<b>只做装配与转发，不含业务逻辑</b>，且全部依赖由容器注入。
///
/// 各职责组件按官方功能面注册在容器里，本类只把它们组合成单一入口：
/// <list type="bullet">
/// <item><see cref="IQuery"/>（官方 5.1 查询 + 探测 + 采样）—— <c>DataItemReader</c></item>
/// <item><see cref="ISet"/>（官方 5.1 设置）—— <c>DataItemWriter</c></item>
/// <item><see cref="IFileTransfer"/>（官方 5.2 文件）—— <c>FileTransfer</c></item>
/// </list>
///
/// <b>推荐通过 <c>services.AddNcLink(...)</c> 注册</b>（见 <c>NcLink</c>），
/// 构造函数虽是 public、但形参为 internal 类型，因此只应由容器解析。
///
/// 生命周期：持有 <see cref="INcLinkTransport"/> 的所有权，
/// <see cref="DisposeAsync"/> 会断开连接并释放传输。
/// </summary>
public sealed class NcLinkClient : INcLinkClient, IDisposable
{
    private readonly INcLinkTransport _transport;
    private readonly RequestPipeline _pipeline;
    private readonly ISampling _sampling;
    private readonly IQuery _query;
    private readonly ISet _set;

    private bool _disposed;

    /// <summary>
    /// 由依赖注入容器解析（见 <c>AddNcLink</c>）。形参含 internal 类型，因此为 internal——
    /// 请注入 <see cref="INcLinkClient"/> 或 <see cref="NcLinkClient"/>，不要手工 <c>new</c>。
    /// </summary>
    internal NcLinkClient(
        INcLinkTransport transport,
        RequestPipeline pipeline,
        ISampling sampling,
        IQuery query,
        ISet set,
        IFileTransfer files)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
        _sampling = sampling ?? throw new ArgumentNullException(nameof(sampling));
        _query = query ?? throw new ArgumentNullException(nameof(query));
        _set = set ?? throw new ArgumentNullException(nameof(set));
        Files = files ?? throw new ArgumentNullException(nameof(files));

        _transport.StateChanged += OnStateChanged;
    }

    /// <inheritdoc />
    public IFileTransfer Files { get; }

    /// <inheritdoc />
    public NcLinkConnectionState ConnectionState => _pipeline.ConnectionState;

    /// <inheritdoc />
    public event Action<NcLinkConnectionState>? ConnectionStateChanged;

    #region 连接

    /// <inheritdoc />
    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _pipeline.EnsureConnectedAsync(cancellationToken);
    }

    /// <inheritdoc />
    public void RegisterModel(Devices.DeviceModel model)
    {
        ThrowIfDisposed();
        _query.RegisterModel(model);
    }

    #endregion

    #region 查询（官方 5.1 查询）

    /// <inheritdoc />
    public Task<Devices.DeviceModel> ProbeAsync(
        string deviceId, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _query.ProbeAsync(deviceId, timeout, cancellationToken);
    }

    /// <inheritdoc />
    public Task<Devices.DeviceModel> GetModelAsync(
        string deviceId, bool refresh = false, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _query.GetModelAsync(deviceId, refresh, cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<DataItemResult>> QueryAsync(
        string deviceId, IEnumerable<DataItemRequest> items,
        TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _query.QueryAsync(deviceId, items, timeout, cancellationToken);
    }

    /// <inheritdoc />
    public Task<DataItemResult> QueryItemAsync(
        string deviceId, DataItemRequest item,
        TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _query.QueryItemAsync(deviceId, item, timeout, cancellationToken);
    }

    #endregion

    #region 设置（官方 5.1 设置）

    /// <inheritdoc />
    public Task<IReadOnlyList<SetItemResult>> SetAsync(
        string deviceId, IEnumerable<DataItemRequest> items,
        TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _set.SetAsync(deviceId, items, timeout, cancellationToken);
    }

    /// <inheritdoc />
    public Task<bool> SetItemAsync(
        string deviceId, DataItemRequest item,
        TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _set.SetItemAsync(deviceId, item, timeout, cancellationToken);
    }

    #endregion

    #region 采样

    /// <inheritdoc />
    public Task<ISampleSubscription> SubscribeSampleAsync(
        string deviceId, string channelId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _query.SubscribeSampleAsync(deviceId, channelId, cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ISampleSubscription>> SubscribeAllSamplesAsync(
        string deviceId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _query.SubscribeAllSamplesAsync(deviceId, cancellationToken);
    }

    #endregion

    #region 释放

    private void OnStateChanged(NcLinkConnectionState state) => ConnectionStateChanged?.Invoke(state);

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(NcLinkClient));
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _transport.StateChanged -= OnStateChanged;
        _pipeline.Dispose();

        await _sampling.UnsubscribeAllAsync().ConfigureAwait(false);
        await _transport.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// 同步释放，转调 <see cref="DisposeAsync"/>。
    ///
    /// <b>必须同时实现 <see cref="IDisposable"/></b>：只实现
    /// <see cref="IAsyncDisposable"/> 的单例会让 <c>ServiceProvider.Dispose()</c>
    /// 直接抛 <see cref="InvalidOperationException"/>，
    /// 而 <c>using var provider = services.BuildServiceProvider()</c>
    /// 是消费者很自然会写的代码。
    /// </summary>
    public void Dispose()
        => DisposeAsync().AsTask().GetAwaiter().GetResult();

    #endregion
}

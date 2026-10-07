namespace NcLink.Protocol.Contracts;

/// <summary>
/// NC-Link 主题契约。实现由 <c>NcLink.Protocol</c> 提供。
/// 单独抽出接口是为了让关联器可以在测试中用假实现驱动，而不必了解主题拼接规则。
/// </summary>
public interface INcLinkTopics
{
    /// <summary>取值请求：<c>Query/Request/{deviceId}</c>。</summary>
    string QueryRequest(string deviceId);

    /// <summary>取值响应：<c>Query/Response/{deviceId}</c>。</summary>
    string QueryResponse(string deviceId);

    /// <summary>设值请求：<c>Set/Request/{deviceId}</c>。</summary>
    string SetRequest(string deviceId);

    /// <summary>设值响应：<c>Set/Response/{deviceId}</c>。</summary>
    string SetResponse(string deviceId);

    /// <summary>探测请求：<c>Probe/Query/Request/{deviceId}</c>。</summary>
    string ProbeQueryRequest(string deviceId);

    /// <summary>探测响应：<c>Probe/Query/Response/{deviceId}</c>。</summary>
    string ProbeQueryResponse(string deviceId);

    /// <summary>探测设值请求：<c>Probe/Set/Request/{deviceId}</c>。</summary>
    string ProbeSetRequest(string deviceId);

    /// <summary>探测设值响应：<c>Probe/Set/Response/{deviceId}</c>。</summary>
    string ProbeSetResponse(string deviceId);

    /// <summary>版本探测：<c>Probe/Version/{deviceId}</c>。</summary>
    string ProbeVersion(string deviceId);

    /// <summary>
    /// 版本探测响应：<c>Probe/Version/Response/{deviceId}</c>。
    /// 实测确认官方设备<b>订阅</b>这条（设备发版本响应、平台收）。
    /// </summary>
    string ProbeVersionResponse(string deviceId);

    /// <summary>
    /// 采样数据主题：<c>Sample/{deviceId}/{channelId}</c>。
    ///
    /// <b>主题含设备 SN</b>（实测官方设备确认，见
    /// <c>docs/official-simulator-verified-protocol.md</c>）。
    /// 因此采样归属是确定的，不需要任何"通道 id 歧义"启发式判断。
    /// </summary>
    string Sample(string deviceId, string channelId);

    /// <summary>设备注册：<c>Register/Request</c>（无设备后缀）。</summary>
    string RegisterRequest { get; }

    /// <summary>
    /// 注册响应：<c>Register/Response/{deviceId}</c>。
    /// 实测确认官方设备<b>订阅</b>这条（设备发注册请求、平台回它表示受理）。
    /// </summary>
    string RegisterResponse(string deviceId);

    /// <summary>响应主题通配符过滤器，用于一个连接管理多台设备。</summary>
    IReadOnlyList<string> AllResponseFilters { get; }

    /// <summary>
    /// 由请求主题推导对应的响应主题；无法推导时返回 null。
    /// 用于发布前先订阅响应主题，避免响应早于订阅到达。
    /// </summary>
    string? ResponseTopicFor(string requestTopic, string deviceId);

    /// <summary>
    /// 判断收到的主题是否属于 <paramref name="requestTopic"/> 的响应——
    /// 关联器用它排除其它设备的响应，避免迟到响应被误领。
    /// </summary>
    bool IsResponseFor(string arrivedTopic, string requestTopic, string? deviceId);
}

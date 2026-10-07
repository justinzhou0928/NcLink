using NcLink.Protocol.Contracts;

namespace NcLink.Protocol;

/// <summary>
/// 主题前缀常量。
///
/// 取值来自官方核心库 <c>cn.nerc.nclink.core.general.Topic</c>：
/// 主题尾部的 <c>{deviceId}</c> 就是设备 SN 码。
/// 设备侧实现通常需要用前缀判断"收到的是哪一类请求"。
/// </summary>
public static class NcLinkTopicPrefixes
{
    public const string ProbeQueryRequest = "Probe/Query/Request/";
    public const string ProbeQueryResponse = "Probe/Query/Response/";
    public const string ProbeSetRequest = "Probe/Set/Request/";
    public const string ProbeSetResponse = "Probe/Set/Response/";
    public const string QueryRequest = "Query/Request/";
    public const string QueryResponse = "Query/Response/";
    public const string SetRequest = "Set/Request/";
    public const string SetResponse = "Set/Response/";
    public const string Sample = "Sample/";
    public const string ProbeVersion = "Probe/Version/";

    /// <summary>
    /// 版本探测响应。官方设备订阅的是它（实测 <c>emqx_ctl topics list</c> 确认）——
    /// 即设备<b>发</b>版本响应、平台<b>收</b>。
    /// </summary>
    public const string ProbeVersionResponse = "Probe/Version/Response/";

    /// <summary>注册请求没有设备后缀。</summary>
    public const string RegisterRequest = "Register/Request";

    /// <summary>
    /// 注册响应。官方设备订阅的是它（带设备后缀）——
    /// 即设备发注册请求、平台回这条表示受理。
    /// </summary>
    public const string RegisterResponse = "Register/Response/";
}

/// <summary>
/// NC-Link 主题规则（实例实现，便于在测试中替换）。
/// </summary>
public sealed class NcLinkTopics : INcLinkTopics
{
    public const string ProbeQueryRequestPrefix = NcLinkTopicPrefixes.ProbeQueryRequest;
    public const string ProbeQueryResponsePrefix = NcLinkTopicPrefixes.ProbeQueryResponse;
    public const string ProbeSetRequestPrefix = NcLinkTopicPrefixes.ProbeSetRequest;
    public const string ProbeSetResponsePrefix = NcLinkTopicPrefixes.ProbeSetResponse;
    public const string QueryRequestPrefix = NcLinkTopicPrefixes.QueryRequest;
    public const string QueryResponsePrefix = NcLinkTopicPrefixes.QueryResponse;
    public const string SetRequestPrefix = NcLinkTopicPrefixes.SetRequest;
    public const string SetResponsePrefix = NcLinkTopicPrefixes.SetResponse;
    public const string SamplePrefix = NcLinkTopicPrefixes.Sample;
    public const string ProbeVersionPrefix = NcLinkTopicPrefixes.ProbeVersion;
    public const string ProbeVersionResponsePrefix = NcLinkTopicPrefixes.ProbeVersionResponse;
    public const string RegisterRequestTopic = NcLinkTopicPrefixes.RegisterRequest;
    public const string RegisterResponsePrefix = NcLinkTopicPrefixes.RegisterResponse;

    /// <summary>单例；规则是纯函数，无需多实例。</summary>
    public static readonly NcLinkTopics Default = new();

    public string QueryRequest(string deviceId) => WithDevice(QueryRequestPrefix, deviceId);

    public string QueryResponse(string deviceId) => WithDevice(QueryResponsePrefix, deviceId);

    public string SetRequest(string deviceId) => WithDevice(SetRequestPrefix, deviceId);

    public string SetResponse(string deviceId) => WithDevice(SetResponsePrefix, deviceId);

    public string ProbeQueryRequest(string deviceId) => WithDevice(ProbeQueryRequestPrefix, deviceId);

    public string ProbeQueryResponse(string deviceId) => WithDevice(ProbeQueryResponsePrefix, deviceId);

    public string ProbeSetRequest(string deviceId) => WithDevice(ProbeSetRequestPrefix, deviceId);

    public string ProbeSetResponse(string deviceId) => WithDevice(ProbeSetResponsePrefix, deviceId);

    public string ProbeVersion(string deviceId) => WithDevice(ProbeVersionPrefix, deviceId);

    /// <summary>版本探测响应主题（设备发、平台收）。</summary>
    public string ProbeVersionResponse(string deviceId) => WithDevice(ProbeVersionResponsePrefix, deviceId);

    /// <summary>
    /// 采样数据主题：<c>Sample/{deviceId}/{channelId}</c>。
    ///
    /// 实测官方设备确认主题<b>含设备 SN</b>——这一点曾经搞错（一度以为只有通道 id，
    /// 并据此设计了"通道歧义则丢帧"的机制），导致订阅后永远收不到帧。
    /// 见 <c>docs/official-simulator-verified-protocol.md</c>。
    /// </summary>
    public string Sample(string deviceId, string channelId)
        => WithDevice(SamplePrefix, $"{deviceId}/{channelId}");

    public string RegisterRequest => RegisterRequestTopic;

    /// <summary>注册响应主题（带设备后缀，设备订阅它）。</summary>
    public string RegisterResponse(string deviceId) => WithDevice(RegisterResponsePrefix, deviceId);

    /// <summary>
    /// 连接建立时批量订阅的响应主题通配符。
    ///
    /// <b>刻意不含采样主题</b>：采样是按通道逐个显式订阅的
    /// （<c>SampleSubscriptions.SubscribeAsync</c> → <c>Sample/{deviceId}/{channelId}</c>）。
    /// 若这里再加一条 <c>Sample/#</c>，同一帧会命中两个订阅，
    /// MQTT 按匹配的订阅各投递一次 → <b>每帧收到两次</b>。
    /// 这是实测发现的（官方 4 帧 / 我们 8 帧）。
    /// </summary>
    public IReadOnlyList<string> AllResponseFilters { get; } =
    [
        ProbeQueryResponsePrefix + "+",
        ProbeSetResponsePrefix + "+",
        QueryResponsePrefix + "+",
        SetResponsePrefix + "+",

        // 下面两条是实测补上的：官方设备订阅 Register/Response/{SN} 与
        // Probe/Version/Response/{SN}，漏订阅就收不到这两类报文。
        RegisterResponsePrefix + "+",
        ProbeVersionResponsePrefix + "+"
    ];

    public string? ResponseTopicFor(string requestTopic, string deviceId) => requestTopic switch
    {
        _ when requestTopic.StartsWith(ProbeQueryRequestPrefix, StringComparison.Ordinal)
            => ProbeQueryResponsePrefix + deviceId,
        _ when requestTopic.StartsWith(ProbeSetRequestPrefix, StringComparison.Ordinal)
            => ProbeSetResponsePrefix + deviceId,
        _ when requestTopic.StartsWith(QueryRequestPrefix, StringComparison.Ordinal)
            => QueryResponsePrefix + deviceId,
        _ when requestTopic.StartsWith(SetRequestPrefix, StringComparison.Ordinal)
            => SetResponsePrefix + deviceId,
        _ => null
    };

    public bool IsResponseFor(string arrivedTopic, string requestTopic, string? deviceId)
    {
        var expectedPrefix = ExpectedResponsePrefix(requestTopic);
        if (expectedPrefix is null) return false;
        if (!arrivedTopic.StartsWith(expectedPrefix, StringComparison.Ordinal)) return false;

        // 响应主题尾部的设备 SN 必须与请求一致（无设备信息时只校验前缀）
        if (string.IsNullOrEmpty(deviceId)) return true;
        var arrivedDevice = arrivedTopic[expectedPrefix.Length..];
        return string.Equals(arrivedDevice, deviceId, StringComparison.Ordinal);
    }

    /// <summary>请求主题前缀 → 响应主题前缀。注意判断顺序：更具体的前缀必须先判断。</summary>
    private static string? ExpectedResponsePrefix(string requestTopic)
    {
        // Probe/Set 必须先于 Set 判断，Probe/Query 必须先于 Query 判断
        if (requestTopic.StartsWith(ProbeSetRequestPrefix, StringComparison.Ordinal)) return ProbeSetResponsePrefix;
        if (requestTopic.StartsWith(ProbeQueryRequestPrefix, StringComparison.Ordinal)) return ProbeQueryResponsePrefix;
        if (requestTopic.StartsWith(SetRequestPrefix, StringComparison.Ordinal)) return SetResponsePrefix;
        if (requestTopic.StartsWith(QueryRequestPrefix, StringComparison.Ordinal)) return QueryResponsePrefix;
        return null;
    }

    private static string WithDevice(string prefix, string deviceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        return prefix + deviceId;
    }
}

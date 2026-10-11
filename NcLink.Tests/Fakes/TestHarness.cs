using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NcLink.Client;
using NcLink.Client.Connection.Engine;
using NcLink.Client.Devices.Interfaces;
using NcLink.Client.Devices.Models;
using NcLink.Client.Devices.Engine;
using NcLink.Client.Query.Engine;
using NcLink.Client.Set.Engine;
using NcLink;
using NcLink.Protocol.Interfaces;
using NcLink.Protocol.Models;
using NcLink.Protocol.Engine;

namespace NcLink.Tests.Fakes;

/// <summary>
/// 服务层测试基座：<b>通过真实的依赖注入容器</b>构建对象图，但把传输换成
/// <see cref="FakeTransport"/>，因此不需要 MQTT Broker。
///
/// 走容器而不是手工 <c>new</c>，是为了让每个单测同时验证
/// <c>AddNcLink</c> 的注册是完整且可解析的——注册漏掉一个依赖，
/// 这里就会失败，而不是等到生产环境运行时空引用。
/// </summary>
internal sealed class TestHarness : IAsyncDisposable
{
    public const string Device = "TEST_DEV";
    public const string RegX = "/MACHINE/CONTROLLER/VARIABLE@REG_X";

    private readonly List<RawRequest> _requests = [];
    private readonly Dictionary<string, Func<string, string>> _responders = new(StringComparer.Ordinal);
    private readonly ServiceProvider _provider;

    public TestHarness()
    {
        Transport = new FakeTransport();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNcLink(
            o => o.RequestTimeout = TimeSpan.FromMilliseconds(300),
            _ => Transport);

        _provider = services.BuildServiceProvider();

        Client = _provider.GetRequiredService<NcLinkClient>();
        Codec = _provider.GetRequiredService<INcLinkCodec>();
        Topics = _provider.GetRequiredService<INcLinkTopics>();
        Pipeline = _provider.GetRequiredService<RequestPipeline>();
        Models = _provider.GetRequiredService<DeviceModelCache>();
        Reader = _provider.GetRequiredService<DataItemReader>();
        Writer = _provider.GetRequiredService<DataItemWriter>();
        Sampling = _provider.GetRequiredService<SampleSubscriptions>();

        Model = SampleDeviceModel.Parse(Device);
        Models.Set(Model);

        Transport.OnPublish = OnPublishAsync;
    }

    public FakeTransport Transport { get; }

    /// <summary>容器解析出的门面（与生产代码同一条装配路径）。</summary>
    public NcLinkClient Client { get; }

    public INcLinkCodec Codec { get; }

    public INcLinkTopics Topics { get; }

    internal RequestPipeline Pipeline { get; }

    public DeviceModel Model { get; }

    internal DeviceModelCache Models { get; }

    internal DataItemReader Reader { get; }

    internal DataItemWriter Writer { get; }

    internal SampleSubscriptions Sampling { get; }

    /// <summary>收到的请求（按顺序）。</summary>
    public IReadOnlyList<RawRequest> Requests => _requests;

    public string QueryTopic => Topics.QueryRequest(Device);

    public string SetTopic => Topics.SetRequest(Device);

    /// <summary>解码最后一次查询请求。</summary>
    public QueryRequestMessage? LastQuery => Decode<QueryRequestMessage>(QueryTopic);

    /// <summary>解码最后一次设置请求。</summary>
    public SetRequestMessage? LastSet => Decode<SetRequestMessage>(SetTopic);

    #region 响应注入

    /// <summary>为某个请求主题注册固定响应。</summary>
    public void Respond(string requestTopic, string responseJson)
        => _responders[requestTopic] = _ => responseJson;

    /// <summary>
    /// 记录请求但<b>不</b>注入任何响应（设备静默）。
    /// 用于测试超时路径——不能用会抛异常的 responder，那样异常会从发布路径冒出来。
    /// </summary>
    public void RespondSilently(string requestTopic)
        => _responders[requestTopic] = _ => string.Empty;

    /// <summary>清空某主题的响应注册（等价于设备不再回应）。</summary>
    public void StopResponding(string requestTopic) => _responders.Remove(requestTopic);

    /// <summary>为某个请求主题注册动态响应（可读取请求文本里的 <c>@id</c>）。</summary>
    public void Respond(string requestTopic, Func<string, string> factory)
        => _responders[requestTopic] = factory;

    /// <summary>让查询返回指定数据项的值。</summary>
    public void RespondQuery(string itemId, string valuesJson)
        => Respond(QueryTopic, request => QueryResponse(itemId, valuesJson, CorrelationIdOf(request)));

    /// <summary>让设置返回成功。</summary>
    public void RespondSetOk(string itemId)
        => Respond(SetTopic, request => SetResponse(itemId, "OK", CorrelationIdOf(request)));

    /// <summary>让设置返回失败。</summary>
    public void RespondSetNg(string itemId)
        => Respond(SetTopic, request => SetResponse(itemId, "NG", CorrelationIdOf(request)));

    /// <summary>构造查询响应报文。</summary>
    public static string QueryResponse(string itemId, string valuesJson, string? correlationId = null)
        => $$"""{"@id":"{{correlationId}}","values":[{"id":"{{itemId}}","code":"OK","values":{{valuesJson}}}]}""";

    /// <summary>构造设置响应报文。</summary>
    public static string SetResponse(string itemId, string code = "OK", string? correlationId = null)
        => $$"""{"@id":"{{correlationId}}","results":[{"id":"{{itemId}}","code":"{{code}}"}]}""";

    /// <summary>取请求文本里的 <c>@id</c>，让响应能被严格匹配认领。</summary>
    public static string CorrelationIdOf(string requestText)
    {
        using var document = JsonDocument.Parse(requestText);
        return document.RootElement.TryGetProperty("@id", out var id)
            ? id.ValueKind == JsonValueKind.String ? id.GetString() ?? string.Empty : id.ToString()
            : string.Empty;
    }
    #endregion

    #region 内部

    private TMessage? Decode<TMessage>(string topic) where TMessage : NcLinkEnvelope
    {
        var request = _requests.LastOrDefault(r => r.Topic == topic);
        return string.IsNullOrEmpty(request.Text) ? null : NcLinkJson.Deserialize<TMessage>(request.Text);
    }

    private Task OnPublishAsync(string topic, byte[] payload, CancellationToken cancellationToken)
    {
        var text = Encoding.UTF8.GetString(payload);
        _requests.Add(new RawRequest(topic, text));

        if (!_responders.TryGetValue(topic, out var factory)) return Task.CompletedTask;

        var responseTopic = Topics.ResponseTopicFor(topic, Device);
        if (string.IsNullOrEmpty(responseTopic))
            throw new InvalidOperationException($"主题 {topic} 没有对应的响应主题。");

        var responseJson = factory(text);
        if (string.IsNullOrEmpty(responseJson)) return Task.CompletedTask;   // 设备静默

        LastInjected = responseJson;
        return Transport.InjectAsync(responseTopic, responseJson);
    }

    /// <summary>最近一次注入的响应文本（诊断用）。</summary>
    public string LastInjected { get; private set; } = string.Empty;

    public async ValueTask DisposeAsync()
    {
        // 容器持有全部单例（含客户端与传输），释放容器即完成收尾
        await _provider.DisposeAsync().ConfigureAwait(false);
    }

    public readonly record struct RawRequest(string Topic, string Text);

    /// <summary>该基座测试不涉及探测（模型直接注入）。</summary>
    private sealed class NoopProbe : IDeviceProbe
    {
        public Task<string> ProbeModelJsonAsync(string deviceId, TimeSpan? timeout, CancellationToken cancellationToken)
            => throw new InvalidOperationException("本基座不测试探测路径，请直接注入模型。");
    }
}
    #endregion

using System.Text.Json;
using NcLink.Protocol.Contracts;
using NcLink.Tests.Fakes;
using NcLink.Protocol;
using NcLink.Client.Connection.Engine;
using NcLink.Client.Connection.Exceptions;
using NcLink.Client.Connection.Options;
using Microsoft.Extensions.Logging.Abstractions;
namespace NcLink.Tests.Client;

/// <summary>
/// 关联 id 的跨实例唯一性测试。
///
/// 背景：关联 id 曾用"实例级计数器"（1、2、3…）。不同客户端实例会重号，
/// 而响应匹配按 <c>"@id":"&lt;id&gt;"</c> 文本比对，于是可能认领到<b>别的实例</b>的响应——
/// 表现为请求失败（设备明明在线却报"设备不可用"），且只在并发场景偶发。
/// </summary>
public sealed class CorrelationIdUniquenessTests
{
    private const string ItemId = "01035430";
    private const string DeviceA = "UNIQ_A";
    private const string DeviceB = "UNIQ_B";

    [Fact]
    public async Task TwoClients_SharingBroker_ShouldNotCrossClaim()
    {
        // 两个关联器共用一条"总线"，模拟同一 Broker 上的两个客户端
        var bus = new SharedBus();

        var transportA = bus.Register();
        var transportB = bus.Register();

        var topics = NcLinkTopics.Default;
        var options = new CorrelationOptions();

        var correlatorA = new RequestCorrelator(transportA, JsonNcLinkCodec.Default, topics, options, TimeSpan.FromSeconds(2), TimeProvider.System, NullLogger<RequestCorrelator>.Instance);
        var correlatorB = new RequestCorrelator(transportB, JsonNcLinkCodec.Default, topics, options, TimeSpan.FromSeconds(2), TimeProvider.System, NullLogger<RequestCorrelator>.Instance);

        // 两个客户端各自请求自己的设备；两个请求的数据项 id 相同，
        // 因此只有靠关联 id 与响应主题才能区分。
        var requestA = new QueryRequestMessage { Ids = [Item(ItemId)] };
        var requestB = new QueryRequestMessage { Ids = [Item(ItemId)] };

        var taskA = correlatorA.SendAsync(requestA, Context(DeviceA, ItemId));
        var taskB = correlatorB.SendAsync(requestB, Context(DeviceB, ItemId));

        // 两个请求的关联 id 必须不同
        Assert.NotEqual(requestA.CorrelationId, requestB.CorrelationId);

        // 让设备各自按请求里的 @id 回应
        await bus.DeliverAsync(topics.QueryResponse(DeviceA), Response(ItemId, requestA.CorrelationId));
        await bus.DeliverAsync(topics.QueryResponse(DeviceB), Response(ItemId, requestB.CorrelationId));

        var resultA = await taskA;
        var resultB = await taskB;

        Assert.Equal(topics.QueryResponse(DeviceA), resultA.Topic);
        Assert.Equal(topics.QueryResponse(DeviceB), resultB.Topic);
    }

    [Fact]
    public async Task MultipleInstances_ShouldProduceDistinctCorrelationIds()
    {
        var bus = new SharedBus();
        var topics = NcLinkTopics.Default;
        var options = new CorrelationOptions();

        var seen = new HashSet<string>(StringComparer.Ordinal);

        // 连续创建多个实例，每个发一次请求（都收不到响应），收集它们的关联 id
        for (var i = 0; i < 6; i++)
        {
            var transport = bus.Register();
            var correlator = new RequestCorrelator(transport, JsonNcLinkCodec.Default, topics, options, TimeSpan.FromMilliseconds(120), TimeProvider.System, NullLogger<RequestCorrelator>.Instance);
            var request = new QueryRequestMessage { Ids = [Item(ItemId)] };

            // 设备从未出现过 → 报"设备不可用"（比"超时"更可操作）
            await Assert.ThrowsAnyAsync<NcLinkException>(() =>
                correlator.SendAsync(request, Context($"DEV_{i}", ItemId)));

            var correlationId = request.CorrelationId;
            Assert.False(string.IsNullOrEmpty(correlationId),
                $"第 {i + 1} 个实例的请求没有写入关联 id。");

            Assert.True(seen.Add(correlationId!),
                $"关联 id 重复：{correlationId}（多个实例必须全局唯一）");
        }
    }

    [Fact]
    public async Task ResponseForOtherClient_ShouldBeIgnored()
    {
        // 场景：B 在等自己设备的响应期间，A 的响应到达 A 的主题。
        // 由于响应携带的 @id 既不属于 B、响应主题也不属于 B，B 绝不能被满足。
        var bus = new SharedBus();
        var transportA = bus.Register();
        var transportB = bus.Register();

        var topics = NcLinkTopics.Default;
        var options = new CorrelationOptions();

        var correlatorA = new RequestCorrelator(transportA, JsonNcLinkCodec.Default, topics, options, TimeSpan.FromMilliseconds(500), TimeProvider.System, NullLogger<RequestCorrelator>.Instance);
        var correlatorB = new RequestCorrelator(transportB, JsonNcLinkCodec.Default, topics, options, TimeSpan.FromMilliseconds(500), TimeProvider.System, NullLogger<RequestCorrelator>.Instance);

        static bool HasItem(RequestContext ctx) => ctx.ResponseText.Contains($"\"{ItemId}\"", StringComparison.Ordinal);

        var requestA = new QueryRequestMessage { Ids = [Item(ItemId)] };
        var requestB = new QueryRequestMessage { Ids = [Item(ItemId)] };

        var taskA = correlatorA.SendAsync(requestA, Context(DeviceA, ItemId), fallbackMatcher: HasItem);
        var taskB = correlatorB.SendAsync(requestB, Context(DeviceB, ItemId), fallbackMatcher: HasItem);

        // 只回应 A，且用 A 自己的 @id
        await bus.DeliverAsync(topics.QueryResponse(DeviceA), Response(ItemId, requestA.CorrelationId));

        Assert.Equal(topics.QueryResponse(DeviceA), (await taskA).Topic);
        await Assert.ThrowsAnyAsync<NcLinkException>(() => taskB);
    }

    [Fact]
    public async Task ResponseWithMismatchedCorrelationId_ShouldNeverBeClaimedByFallback()
    {
        // 关键回归：响应携带了 @id，但不等于本请求的 @id。
        // 即使响应主题归属正确、数据项 id 也吻合，也**绝不允许**降级认领——
        // 否则设备掉线时的迟到响应（属于上一个操作）会被当成本次结果，
        // 典型症状是文件下载拿到非十六进制内容而抛 FormatException。
        var bus = new SharedBus();
        var transport = bus.Register();
        var topics = NcLinkTopics.Default;
        var options = new CorrelationOptions();

        var correlator = new RequestCorrelator(transport, JsonNcLinkCodec.Default, topics, options, TimeSpan.FromMilliseconds(300), TimeProvider.System, NullLogger<RequestCorrelator>.Instance);

        static bool HasItem(RequestContext ctx) => ctx.ResponseText.Contains($"\"{ItemId}\"", StringComparison.Ordinal);

        var request = new QueryRequestMessage { Ids = [Item(ItemId)] };

        var task = correlator.SendAsync(request, Context(DeviceA, ItemId), fallbackMatcher: HasItem);

        // 投递一条"主题对、数据项对，但 @id 是别人的"响应
        await bus.DeliverAsync(topics.QueryResponse(DeviceA), Response(ItemId, "someone-elses-id"));

        await Assert.ThrowsAnyAsync<NcLinkException>(() => task);
    }

    [Fact]
    public async Task SilentDevice_AfterBeenSeen_ShouldReportTimeoutNotUnavailable()
    {
        // 设备出现过之后不再回应 → 语义应是"超时"，而不是"设备不可用"
        var bus = new SharedBus();
        var transport = bus.Register();
        var topics = NcLinkTopics.Default;
        var options = new CorrelationOptions();

        var correlator = new RequestCorrelator(transport, JsonNcLinkCodec.Default, topics, options, TimeSpan.FromMilliseconds(150), TimeProvider.System, NullLogger<RequestCorrelator>.Instance);

        // 先制造一次"设备出现过"（一条带正确关联 id 的响应）
        var warmup = new QueryRequestMessage { Ids = [Item(ItemId)] };
        var warmupTask = correlator.SendAsync(warmup, Context(DeviceA, ItemId));
        await bus.DeliverAsync(topics.QueryResponse(DeviceA), Response(ItemId, warmup.CorrelationId));
        Assert.EndsWith(DeviceA, (await warmupTask).Topic, StringComparison.Ordinal);
        // 这一次不再回应
        var exception = await Assert.ThrowsAsync<NcLinkTimeoutException>(() =>
            correlator.SendAsync(new QueryRequestMessage { Ids = [Item(ItemId)] }, Context(DeviceA, ItemId)));

        Assert.Contains(DeviceA, exception.Topic);
    }

    private static QueryRequestItemMessage Item(string id)
        => new() { Id = id, Params = new QueryParamsMessage { Indexes = ["0"] } };

    private static RequestContext Context(string deviceId, string itemId)
        => new(NcLinkTopics.Default.QueryRequest(deviceId), correlationId: string.Empty, deviceId, [itemId]);

    private static string Response(string itemId, string? correlationId)
        => NcLinkJson.Serialize(new QueryResponseMessage
        {
            CorrelationId = correlationId,
            Values =
            [
                new QueryResponseItemMessage
                {
                    Id = itemId,
                    Code = NcLinkCodeNames.Ok,
                    Values = [JsonSerializer.SerializeToElement(1)]
                }
            ]
        });

    /// <summary>
    /// 共享总线：让多个 <see cref="FakeTransport"/> 互通（模拟同一 Broker 上的多个客户端）。
    /// </summary>
    private sealed class SharedBus
    {
        private readonly List<FakeTransport> _transports = [];

        public FakeTransport Register()
        {
            var transport = new FakeTransport();
            _transports.Add(transport);
            return transport;
        }

        /// <summary>向所有已注册客户端投递一条报文。</summary>
        public async Task DeliverAsync(string topic, string json)
        {
            foreach (var transport in _transports)
                await transport.InjectAsync(topic, json);
        }
    }
}

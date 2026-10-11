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
/// 请求-响应关联测试。
///
/// 这一组测试对应一个<b>真实发生过的缺陷</b>：早期实现用「报文里出现 deviceId」做兜底匹配，
/// 而 deviceId 也出现在响应主题里，导致设备掉线时的<b>迟到响应</b>被后续请求领走，
/// 表现为「已确认杀死的设备仍返回成功」。
/// 分层重构把这些规则收进独立的 <see cref="RequestCorrelator"/>，于是可以脱离 Broker 直接验证。
/// </summary>
public sealed class RequestCorrelatorTests
{
    private const string DeviceA = "DEV_A";
    private const string DeviceB = "DEV_B";
    private const string ItemRegX = "01035430";

    private static readonly TimeSpan ShortTimeout = TimeSpan.FromMilliseconds(150);
    private static readonly INcLinkTopics Topics = NcLinkTopics.Default;

    private static RequestCorrelator CreateCorrelator(FakeTransport transport, TimeSpan? defaultTimeout = null)
        => new(transport,
            JsonNcLinkCodec.Default,
            Topics,
            new CorrelationOptions(),
            defaultTimeout ?? ShortTimeout,
            TimeProvider.System,
            NullLogger<RequestCorrelator>.Instance);

    private static RequestContext ContextFor(string deviceId, params string[] itemIds)
        => new(Topics.QueryRequest(deviceId), deviceId, itemIds);

    private static QueryRequestMessage BuildQuery(string itemId)
        => new()
        {
            Ids = [new QueryRequestItemMessage { Id = itemId, Params = new QueryParamsMessage { Indexes = ["0-9"] } }]
        };

    private static string QueryResponse(string itemId, string? correlationId, string deviceId = DeviceA)
    {
        var response = new QueryResponseMessage
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
        };

        return NcLinkJson.Serialize(response);
    }

    private static string CorrelationIdOf(byte[] payload)
        => NcLinkJson.Deserialize<QueryRequestMessage>(System.Text.Encoding.UTF8.GetString(payload))
           ?.CorrelationId ?? string.Empty;

    /// <summary>
    /// 让设备"出现过"一次：关联器据此把"从未见过"与"见过但无响应"区分开，
    /// 前者报 <see cref="NcLinkDeviceUnavailableException"/>，后者报 <see cref="NcLinkTimeoutException"/>。
    /// </summary>
    private static Task MarkDeviceActiveAsync(FakeTransport transport, string deviceId)
        => transport.InjectAsync(Topics.QueryResponse(deviceId), QueryResponse(ItemRegX, correlationId: null, deviceId));

    [Fact]
    public async Task SendAsync_ResponseWithMatchingCorrelationId_ShouldSucceed()
    {
        var transport = new FakeTransport();
        transport.OnPublish = async (topic, payload, _) =>
            await transport.InjectAsync(Topics.QueryResponse(DeviceA),
                QueryResponse(ItemRegX, CorrelationIdOf(payload)));

        var correlator = CreateCorrelator(transport);

        var response = await correlator.SendAsync(
            BuildQuery(ItemRegX),
            ContextFor(DeviceA, ItemRegX),
            strictMatcher: ctx => ctx.ResponseText.Contains("\"" + ItemRegX + "\"", StringComparison.Ordinal));

        Assert.Equal(Topics.QueryResponse(DeviceA), response.Topic);
    }

    [Fact]
    public async Task SendAsync_OtherDeviceLateResponse_ShouldNotBeClaimed()
    {
        // 这是修复的核心场景：
        // 一条属于 DEV_B、但携带我们数据项 id 的响应（模拟迟到响应），
        // 绝不能被针对 DEV_A 的请求认领。
        var transport = new FakeTransport();
        var correlator = CreateCorrelator(transport);

        transport.OnPublish = async (topic, _, _) =>
        {
            // 先注入"别的设备"的迟到响应
            await transport.InjectAsync(Topics.QueryResponse(DeviceB),
                QueryResponse(ItemRegX, correlationId: null, deviceId: DeviceB));
            // 再注入我们自己的正常响应
            await transport.InjectAsync(Topics.QueryResponse(DeviceA),
                QueryResponse(ItemRegX, correlationId: null));
        };

        var response = await correlator.SendAsync(
            BuildQuery(ItemRegX),
            ContextFor(DeviceA, ItemRegX),
            fallbackMatcher: ctx => ctx.ResponseText.Contains("\"" + ItemRegX + "\"", StringComparison.Ordinal));

        // 必须是 DEV_A 的那一条
        Assert.Equal(Topics.QueryResponse(DeviceA), response.Topic);
    }

    [Fact]
    public async Task SendAsync_NoResponse_ShouldTimeout()
    {
        var transport = new FakeTransport();
        var correlator = CreateCorrelator(transport);
        await MarkDeviceActiveAsync(transport, DeviceA);   // 设备出现过，但这次不回应

        var exception = await Assert.ThrowsAsync<NcLinkTimeoutException>(() =>
            correlator.SendAsync(BuildQuery(ItemRegX), ContextFor(DeviceA, ItemRegX)));

        Assert.Contains(DeviceA, exception.Topic);
    }

    [Fact]
    public async Task SendAsync_OtherDeviceResponseOnly_ShouldTimeout()
    {
        // 只有别的设备的响应 → 我们的请求必须超时，而不是认领它
        var transport = new FakeTransport();
        var correlator = CreateCorrelator(transport);
        await MarkDeviceActiveAsync(transport, DeviceA);

        transport.OnPublish = async (_, _, _) =>
            await transport.InjectAsync(Topics.QueryResponse(DeviceB),
                QueryResponse(ItemRegX, correlationId: null, deviceId: DeviceB));

        await Assert.ThrowsAsync<NcLinkTimeoutException>(() =>
            correlator.SendAsync(
                BuildQuery(ItemRegX),
                ContextFor(DeviceA, ItemRegX),
                fallbackMatcher: ctx => ctx.ResponseText.Contains("\"" + ItemRegX + "\"", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task SendAsync_FallbackMatcherNotUsed_WhenStrictMatches()
    {
        // 严格匹配命中时，不应该再走降级分支
        var transport = new FakeTransport();
        var correlator = CreateCorrelator(transport);

        var fallbackUsed = false;

        transport.OnPublish = async (_, payload, _) =>
            await transport.InjectAsync(Topics.QueryResponse(DeviceA),
                QueryResponse(ItemRegX, CorrelationIdOf(payload)));

        await correlator.SendAsync(
            BuildQuery(ItemRegX),
            ContextFor(DeviceA, ItemRegX),
            fallbackMatcher: _ => { fallbackUsed = true; return true; });

        Assert.False(fallbackUsed);
    }

    [Fact]
    public async Task SendAsync_ShouldNotSubscribePerRequest()
    {
        var transport = new FakeTransport();
        var correlator = CreateCorrelator(transport);
        await MarkDeviceActiveAsync(transport, DeviceA);

        string[]? subscriptionsAtPublish = null;
        transport.OnPublish = (_, _, _) =>
        {
            subscriptionsAtPublish = transport.Subscriptions.ToArray();
            return Task.CompletedTask;
        };

        await Assert.ThrowsAsync<NcLinkTimeoutException>(() =>
            correlator.SendAsync(BuildQuery(ItemRegX), ContextFor(DeviceA, ItemRegX)));

        // 关联器不得按请求订阅响应主题（通配符已在连接时由管线订好），
        // 否则同一帧会被投递两次。理由见 AGENTS.md §2.5。
        Assert.NotNull(subscriptionsAtPublish);
        Assert.DoesNotContain(Topics.QueryResponse(DeviceA), subscriptionsAtPublish!);
        Assert.Empty(subscriptionsAtPublish!);
    }

    [Fact]
    public async Task SendAsync_ShouldConnectWhenNotConnected()
    {
        var transport = new FakeTransport();
        var correlator = CreateCorrelator(transport);
        await MarkDeviceActiveAsync(transport, DeviceA);

        await Assert.ThrowsAsync<NcLinkTimeoutException>(() =>
            correlator.SendAsync(BuildQuery(ItemRegX), ContextFor(DeviceA, ItemRegX)));

        Assert.Equal(1, transport.ConnectCount);
    }

    [Fact]
    public async Task SendAsync_ShouldStampCorrelationIdOnRequest()
    {
        var transport = new FakeTransport();
        var correlator = CreateCorrelator(transport);
        await MarkDeviceActiveAsync(transport, DeviceA);

        var request = BuildQuery(ItemRegX);

        await Assert.ThrowsAsync<NcLinkTimeoutException>(() =>
            correlator.SendAsync(request, ContextFor(DeviceA, ItemRegX)));

        Assert.False(string.IsNullOrEmpty(request.CorrelationId));
    }

    [Fact]
    public async Task SendAsync_ReusedRequestObject_ShouldGetFreshCorrelationId()
    {
        // 复用同一个请求对象时，必须每次指派新的关联 id，
        // 否则两个在途请求会互相认领响应。
        var transport = new FakeTransport();
        var correlator = CreateCorrelator(transport);
        await MarkDeviceActiveAsync(transport, DeviceA);

        var request = BuildQuery(ItemRegX);

        await Assert.ThrowsAsync<NcLinkTimeoutException>(() =>
            correlator.SendAsync(request, ContextFor(DeviceA, ItemRegX)));
        var first = request.CorrelationId;

        await Assert.ThrowsAsync<NcLinkTimeoutException>(() =>
            correlator.SendAsync(request, ContextFor(DeviceA, ItemRegX)));
        var second = request.CorrelationId;

        Assert.NotEqual(first, second);
    }

    [Fact]
    public async Task SendAsync_DeviceUnavailable_ShouldReportOfflineRatherThanTimeout()
    {
        // 设备从未在本会话出现过 → 报"设备不可用"，比"超时"更可操作
        var transport = new FakeTransport();
        var correlator = CreateCorrelator(transport);

        var exception = await Assert.ThrowsAsync<NcLinkDeviceUnavailableException>(() =>
            correlator.SendAsync(BuildQuery(ItemRegX), ContextFor("NEVER_SEEN_DEVICE", ItemRegX)));

        Assert.Equal("NEVER_SEEN_DEVICE", exception.DeviceId);
    }

    [Fact]
    public async Task SendAsync_AfterDeviceWasActive_ShouldReportTimeout()
    {
        var transport = new FakeTransport();
        var correlator = CreateCorrelator(transport);

        // 先让设备"出现"一次
        await transport.InjectAsync(Topics.QueryResponse(DeviceA), QueryResponse(ItemRegX, correlationId: null));

        var exception = await Assert.ThrowsAsync<NcLinkTimeoutException>(() =>
            correlator.SendAsync(BuildQuery(ItemRegX), ContextFor(DeviceA, ItemRegX)));

        Assert.Contains(DeviceA, exception.Topic);
    }

    [Fact]
    public async Task SendAsync_ConcurrentRequests_ShouldNotCrossClaim()
    {
        // 两个并发请求、同一数据项、不同设备：各自的响应必须回到各自的调用方
        var transport = new FakeTransport();
        var correlator = CreateCorrelator(transport, defaultTimeout: TimeSpan.FromSeconds(2));

        transport.OnPublish = async (topic, payload, _) =>
        {
            var correlationId = CorrelationIdOf(payload);
            var deviceId = topic.EndsWith(DeviceA, StringComparison.Ordinal) ? DeviceA : DeviceB;
            await transport.InjectAsync(Topics.QueryResponse(deviceId),
                QueryResponse(ItemRegX, correlationId, deviceId));
        };

        var taskA = correlator.SendAsync(BuildQuery(ItemRegX), ContextFor(DeviceA, ItemRegX));
        var taskB = correlator.SendAsync(BuildQuery(ItemRegX), ContextFor(DeviceB, ItemRegX));

        var results = await Task.WhenAll(taskA, taskB);

        Assert.Equal(Topics.QueryResponse(DeviceA), results[0].Topic);
        Assert.Equal(Topics.QueryResponse(DeviceB), results[1].Topic);
    }

    [Fact]
    public async Task SendAsync_PendingRequest_ShouldBeRemovedAfterCompletion()
    {
        var transport = new FakeTransport();
        var correlator = CreateCorrelator(transport);

        transport.OnPublish = async (_, payload, _) =>
            await transport.InjectAsync(Topics.QueryResponse(DeviceA),
                QueryResponse(ItemRegX, CorrelationIdOf(payload)));

        await correlator.SendAsync(BuildQuery(ItemRegX), ContextFor(DeviceA, ItemRegX));

        Assert.Equal(0, correlator.PendingCount);
    }
}

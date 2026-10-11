using System.Text.Json;
using NcLink.Tests.Fakes;
using NcLink.Protocol.Models;
using NcLink.Client.Connection;
using NcLink.Client.Query.Models;
using NcLink.Client.Query.Engine;

namespace NcLink.Tests.Client;

/// <summary>
/// <c>SampleSubscriptions</c> 单元测试：<b>不需要 MQTT Broker</b>。
///
/// 采样主题是 <c>Sample/{deviceId}/{channelId}</c>，<b>含设备 SN</b>——
/// 这一点由官方模拟器实测确认（见 <c>docs/official-simulator-verified-protocol.md</c>）。
///
/// 曾有一版实现假设"主题不含 SN"，并据此在通道 id 冲突时丢弃整帧；
/// 那个前提是错的，本文件相应改为验证"按 SN 精确归因"。
/// </summary>
public sealed class SampleSubscriptionsTests
{
    private const string Channel = "test_channel";

    [Fact]
    public async Task Subscribe_ShouldSubscribeSnScopedTopicAndReturnHandle()
    {
        await using var harness = new TestHarness();

        var subscription = await harness.Sampling.SubscribeAsync(TestHarness.Device, Channel);

        Assert.Equal(TestHarness.Device, subscription.DeviceId);
        Assert.Equal(Channel, subscription.ChannelId);

        // 主题必须带 SN：Sample/{SN}/{channel}
        var expected = harness.Topics.Sample(TestHarness.Device, Channel);
        Assert.Equal($"Sample/{TestHarness.Device}/{Channel}", expected);
        Assert.Contains(expected, harness.Transport.Subscriptions);
        Assert.Equal(1, harness.Sampling.Multiplexer.SubscriptionCount);
    }

    [Fact]
    public async Task Subscribe_SameChannelTwice_ShouldReturnSameHandle()
    {
        await using var harness = new TestHarness();

        var first = await harness.Sampling.SubscribeAsync(TestHarness.Device, Channel);
        var second = await harness.Sampling.SubscribeAsync(TestHarness.Device, Channel);

        Assert.Same(first, second);
        Assert.Equal(1, harness.Sampling.Multiplexer.SubscriptionCount);
    }

    [Fact]
    public async Task SubscribeAll_ShouldSubscribeEveryChannelInModel()
    {
        await using var harness = new TestHarness();

        var subscriptions = await harness.Sampling.SubscribeAllAsync(TestHarness.Device);

        Assert.NotEmpty(subscriptions);
        Assert.Equal(harness.Model.SampleChannels.Count, subscriptions.Count);
        foreach (var channel in harness.Model.SampleChannels.Keys)
            Assert.Contains(
                harness.Topics.Sample(TestHarness.Device, channel),
                harness.Transport.Subscriptions);
    }

    [Fact]
    public async Task Unsubscribe_ShouldRemoveRegistrationAndTopic()
    {
        await using var harness = new TestHarness();
        var subscription = await harness.Sampling.SubscribeAsync(TestHarness.Device, Channel);

        await subscription.UnsubscribeAsync();

        Assert.Equal(0, harness.Sampling.Multiplexer.SubscriptionCount);
        Assert.DoesNotContain(
            harness.Topics.Sample(TestHarness.Device, Channel),
            harness.Transport.Subscriptions);
    }

    [Fact]
    public async Task Frame_ShouldBeDeliveredToSubscriber()
    {
        await using var harness = new TestHarness();
        var subscription = await harness.Sampling.SubscribeAsync(TestHarness.Device, Channel);

        SampleFrame? received = null;
        subscription.FrameReceived += (_, frame) => received = frame;

        // 报文体与官方实测一致：data[] 元素只有 data，没有 id
        await InjectAsync(harness, TestHarness.Device, Channel, """[{"data":[1,2,3]}]""");

        Assert.NotNull(received);
        Assert.Equal(TestHarness.Device, received!.DeviceId);
        Assert.Equal(Channel, received.ChannelId);
        Assert.Single(received.Items);
        Assert.Equal([1, 2, 3], received.ValuesAt(0).Select(e => e.GetInt32()).ToArray());
        Assert.Equal(1, subscription.ReceivedCount);
        Assert.NotNull(subscription.LastReceivedAt);
    }

    [Fact]
    public async Task Frame_EmptyDataArray_ShouldStillBeDelivered()
    {
        await using var harness = new TestHarness();
        var subscription = await harness.Sampling.SubscribeAsync(TestHarness.Device, Channel);

        SampleFrame? received = null;
        subscription.FrameReceived += (_, frame) => received = frame;

        // 空数组是有效值（例如"当前无报警"），不能被当成缺失而丢弃
        await InjectAsync(harness, TestHarness.Device, Channel, """[{"data":[]}]""");

        Assert.NotNull(received);
        Assert.Empty(received!.ValuesAt(0));
    }

    [Fact]
    public async Task Frame_NullData_ShouldBeDeliveredAsEmptyItems()
    {
        await using var harness = new TestHarness();
        var subscription = await harness.Sampling.SubscribeAsync(TestHarness.Device, Channel);

        SampleFrame? received = null;
        subscription.FrameReceived += (_, frame) => received = frame;

        // 官方 event_upload 通道实测会出现 data:null
        await InjectAsync(harness, TestHarness.Device, Channel, "null");

        Assert.NotNull(received);
        Assert.Empty(received!.Items);
    }

    [Fact]
    public async Task Frame_NestedObjectValue_ShouldBePreserved()
    {
        await using var harness = new TestHarness();
        var subscription = await harness.Sampling.SubscribeAsync(TestHarness.Device, Channel);

        SampleFrame? received = null;
        subscription.FrameReceived += (_, frame) => received = frame;

        // 报警项实测形如 [[{"number":"01020340","text":"PLC报警"}]]
        await InjectAsync(harness, TestHarness.Device, Channel,
            """[{"data":[[{"number":"01020340","text":"PLC报警"}]]}]""");

        var value = received!.ValuesAt(0)[0];
        Assert.Equal(JsonValueKind.Array, value.ValueKind);
        Assert.Equal("01020340", value[0].GetProperty("number").GetString());
    }

    [Fact]
    public async Task SampleForUnsubscribedChannel_ShouldBeIgnored()
    {
        await using var harness = new TestHarness();
        await harness.Sampling.SubscribeAsync(TestHarness.Device, Channel);

        Assert.False(harness.Sampling.Multiplexer.TryDispatch(
            Raw(harness.Topics.Sample(TestHarness.Device, "nobody_listens"), """[{"data":[1]}]""")));
    }

    [Fact]
    public async Task SameChannelIdOnTwoDevices_ShouldBeAttributedBySn()
    {
        await using var harness = new TestHarness();
        const string shared = "shared_channel";

        // 两台设备注册了同一个通道 id。
        // 主题带 SN，因此归属是确定的——各自只收到发给自己的帧。
        var first = await harness.Sampling.SubscribeAsync("DEV_A", shared);
        var second = await harness.Sampling.SubscribeAsync("DEV_B", shared);

        SampleFrame? gotA = null, gotB = null;
        first.FrameReceived += (_, f) => gotA = f;
        second.FrameReceived += (_, f) => gotB = f;

        await InjectAsync(harness, "DEV_A", shared, """[{"data":[9]}]""");

        Assert.NotNull(gotA);
        Assert.Null(gotB);                        // B 不该收到发给 A 的帧
        Assert.Equal("DEV_A", gotA!.DeviceId);
        Assert.Equal(1, first.ReceivedCount);
        Assert.Equal(0, second.ReceivedCount);
    }

    [Fact]
    public async Task PayloadWithoutChannelId_ShouldFallBackToTopic()
    {
        await using var harness = new TestHarness();
        var subscription = await harness.Sampling.SubscribeAsync(TestHarness.Device, Channel);

        SampleFrame? received = null;
        subscription.FrameReceived += (_, frame) => received = frame;

        // 报文体里没有 id 字段 → 用主题里的通道 id
        await InjectAsync(harness, TestHarness.Device, Channel, """[{"data":[5]}]""", includeChannelId: false);

        Assert.NotNull(received);
        Assert.Equal(Channel, received!.ChannelId);
    }

    [Fact]
    public async Task ItemIds_ShouldComeFromChannelConfig()
    {
        await using var harness = new TestHarness();

        // 必须订阅模型里真实存在的通道——采样报文不带数据项 id，
        // 映射依据只能来自该通道在模型里的 ids 配置
        var channel = harness.Model.SampleChannels.Values.First();
        var subscription = await harness.Sampling.SubscribeAsync(TestHarness.Device, channel.ChannelId);

        SampleFrame? received = null;
        subscription.FrameReceived += (_, frame) => received = frame;

        await InjectAsync(harness, TestHarness.Device, channel.ChannelId, """[{"data":[7]}]""");

        Assert.NotNull(received);
        Assert.Equal(channel.ItemIds, received!.ItemIds);
    }

    [Fact]
    public async Task ItemIds_UnknownChannel_ShouldBeEmpty()
    {
        await using var harness = new TestHarness();
        var subscription = await harness.Sampling.SubscribeAsync(TestHarness.Device, Channel);

        SampleFrame? received = null;
        subscription.FrameReceived += (_, frame) => received = frame;

        // Channel 不在模型里 → 取不到 id 清单，但帧仍应送达（只是无法映射回 id）
        await InjectAsync(harness, TestHarness.Device, Channel, """[{"data":[7]}]""");

        Assert.NotNull(received);
        Assert.Empty(received!.ItemIds);
        Assert.Single(received.Items);
    }

    [Fact]
    public async Task MalformedTopic_ShouldBeRejected()
    {
        await using var harness = new TestHarness();
        await harness.Sampling.SubscribeAsync(TestHarness.Device, Channel);

        // 只有两层（旧的错误形状）→ 无法解析
        Assert.False(harness.Sampling.Multiplexer.TryDispatch(
            Raw("Sample/onlychannel", """[{"data":[1]}]""")));

        // 只有前缀、无设备与通道
        Assert.False(harness.Sampling.Multiplexer.TryDispatch(
            Raw("Sample/", """[{"data":[1]}]""")));
    }

    [Theory]
    [InlineData("Sample/DEV_A/ch0", "DEV_A", "ch0")]
    [InlineData("Sample/SN-1/a/b", "SN-1", "a/b")]     // 通道 id 含斜杠时只在首个分隔符切分
    public void TryParseSampleTopic_ShouldSplitSnAndChannel(
        string topic, string expectedDevice, string expectedChannel)
    {
        Assert.True(SampleMultiplexer.TryParseSampleTopic(topic, out var deviceId, out var channelId));
        Assert.Equal(expectedDevice, deviceId);
        Assert.Equal(expectedChannel, channelId);
    }

    [Theory]
    [InlineData("Sample/ch0")]           // 只有两层
    [InlineData("Sample/DEV_A/")]        // 缺通道
    [InlineData("Sample//ch0")]          // 缺设备
    [InlineData("Query/Request/DEV_A")]  // 不是采样主题
    public void TryParseSampleTopic_Malformed_ShouldFail(string topic)
        => Assert.False(SampleMultiplexer.TryParseSampleTopic(topic, out _, out _));

    [Fact]
    public async Task FrameReceivedHandlerThrows_ShouldSurfaceAsProtocolException()
    {
        await using var harness = new TestHarness();
        var subscription = await harness.Sampling.SubscribeAsync(TestHarness.Device, Channel);
        subscription.FrameReceived += (_, _) => throw new InvalidOperationException("处理器炸了");

        // 分发器把处理器异常包成协议异常，避免一条坏订阅静默吞掉后续帧
        Assert.Throws<NcLinkProtocolException>(() =>
            harness.Sampling.Multiplexer.TryDispatch(
                Raw(harness.Topics.Sample(TestHarness.Device, Channel), """[{"data":[1]}]""")));
    }

    [Fact]
    public async Task UnsubscribeAll_ShouldCleanUpEverySubscription()
    {
        await using var harness = new TestHarness();
        await harness.Sampling.SubscribeAllAsync(TestHarness.Device);
        var count = harness.Sampling.Multiplexer.SubscriptionCount;
        Assert.True(count > 0);

        await harness.Sampling.UnsubscribeAllAsync();

        Assert.Equal(0, harness.Sampling.Multiplexer.SubscriptionCount);
    }

    #region 辅助

    /// <summary>
    /// 构造采样报文，形状与官方实测一致：
    /// <c>{ "id":"通道id", "beginTime":..., "data":[ {"data":[...]} ] }</c>。
    /// <b>内层元素没有 id</b>——官方设备不返回数据项 id。
    /// </summary>
    private static string Payload(string dataJson, string? channelId)
        => channelId is null
            ? $$"""{"beginTime":1700000000000,"data":{{dataJson}}}"""
            : $$"""{"id":"{{channelId}}","beginTime":1700000000000,"data":{{dataJson}}}""";

    private static async Task InjectAsync(
        TestHarness harness, string deviceId, string channelId,
        string dataJson, bool includeChannelId = true)
        => await harness.Transport.InjectAsync(
            harness.Topics.Sample(deviceId, channelId),
            Payload(dataJson, includeChannelId ? channelId : null));

    /// <summary>构造一条采样报文（主题 + 负载），通道 id 取自主题尾段。</summary>
    private static RawMessage Raw(string topic, string dataJson)
        => new(topic, System.Text.Encoding.UTF8.GetBytes(
            Payload(dataJson, topic.StartsWith("Sample/", StringComparison.Ordinal) &&
                              topic.Count(c => c == '/') >= 2
                ? topic[(topic.IndexOf('/', 7) + 1)..]
                : null)));

    #endregion
}

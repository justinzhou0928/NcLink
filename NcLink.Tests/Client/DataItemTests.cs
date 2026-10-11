using NcLink.Protocol.Models;
using NcLink.Tests.Fakes;
using NcLink.Client.Devices.Models;
using NcLink.Client.Devices.Engine;
using NcLink.Client.Query.Models;
using NcLink.Exceptions;

namespace NcLink.Tests.Client;

/// <summary>
/// <c>DataService</c> 单元测试：<b>不需要 MQTT Broker</b>。
///
/// 覆盖"路径 → 数据项 id → 报文参数 → 结果"这条链路，
/// 以及超时、NG、路径不存在等失败路径。
/// </summary>
public sealed class DataItemTests
{
    /// <summary>示例模型里 REG_X 的数据项 id。</summary>
    private static readonly string RegXId = SampleDeviceModel.Parse(TestHarness.Device)
        .GetRequiredId(TestHarness.RegX);

    [Fact]
    public async Task QueryItem_ShouldResolvePathAndParseValues()
    {
        await using var harness = new TestHarness();
        harness.RespondQuery(RegXId, "[1,2,3]");

        var result = await harness.Reader.QueryItemAsync(TestHarness.Device, DataItemRequest.At(TestHarness.RegX));

        Assert.True(result.Succeeded);
        Assert.Equal(RegXId, result.ItemId);
        Assert.Equal([1, 2, 3], result.ToIntArray());
    }

    [Fact]
    public async Task Query_ShouldAcceptPathTagAndRawId()
    {
        // 三种写法都应解析到同一个数据项 id
        string[] spellings =
        [
            TestHarness.RegX,
            "VARIABLE@REG_X",
            RegXId
        ];

        foreach (var spelling in spellings)
        {
            await using var harness = new TestHarness();
            harness.RespondQuery(RegXId, "[7]");

            var result = await harness.Reader.QueryItemAsync(TestHarness.Device, DataItemRequest.At(spelling));

            Assert.Equal(7, result.IntAt(0));
            Assert.Equal(RegXId, harness.LastQuery!.Ids![0].Id);
        }
    }

    [Fact]
    public async Task Query_MultipleItems_ShouldGoInOneRequest()
    {
        await using var harness = new TestHarness();
        var statusId = harness.Model.GetRequiredId("/MACHINE/STATUS");

        harness.Respond(harness.QueryTopic, request =>
            $$"""
              {"@id":"{{TestHarness.CorrelationIdOf(request)}}","values":[
                {"id":"{{RegXId}}","code":"OK","values":[1]},
                {"id":"{{statusId}}","code":"OK","values":["RUN"]}]}
              """);

        var results = await harness.Reader.QueryAsync(TestHarness.Device,
        [
            DataItemRequest.At(TestHarness.RegX),
            DataItemRequest.At("/MACHINE/STATUS")
        ]);

        Assert.Equal(2, results.Count);
        Assert.Equal(2, harness.LastQuery!.Ids!.Count);   // 两个数据项打包进一次请求
        Assert.Single(harness.Requests);                  // 只发了一次请求
    }

    [Fact]
    public async Task Query_IndexRange_ShouldBecomeIndexParam()
    {
        await using var harness = new TestHarness();
        harness.RespondQuery(RegXId, "[0,1,2,3]");

        await harness.Reader.QueryItemAsync(TestHarness.Device, DataItemRequest.At(TestHarness.RegX, 0, 9));

        Assert.Equal(["0-9"], harness.LastQuery!.Ids![0].Params!.Indexes!);
    }

    [Fact]
    public async Task Query_WithOffsetAndLength_ShouldPassThemThrough()
    {
        await using var harness = new TestHarness();
        harness.RespondQuery(RegXId, "[\"abcd\"]");

        await harness.Reader.QueryItemAsync(TestHarness.Device, new DataItemRequest
        {
            Path = TestHarness.RegX,
            Operation = NcLinkOperation.GetValue,
            Keys = ["O0001"],
            Offset = 100,
            Length = 50
        });

        var parameters = harness.LastQuery!.Ids![0].Params!;
        Assert.Equal(100, parameters.Offset);
        Assert.Equal(50, parameters.Length);
        Assert.Equal(["O0001"], parameters.Keys!);
        Assert.Equal("get_value", parameters.Operation);
    }

    [Fact]
    public async Task Query_UnknownPath_ShouldThrowPathNotFound()
    {
        await using var harness = new TestHarness();

        var exception = await Assert.ThrowsAsync<NcLinkModelPathNotFoundException>(() =>
            harness.Reader.QueryItemAsync(TestHarness.Device, DataItemRequest.At("/MACHINE/NO_SUCH_THING")));

        Assert.Equal("/MACHINE/NO_SUCH_THING", exception.Path);
        Assert.Equal(TestHarness.Device, exception.DeviceId);
        Assert.Empty(harness.Requests);   // 解析失败就不该发报文
    }

    [Fact]
    public async Task Query_ItemWithoutValues_ShouldReturnResultWithZeroCount()
    {
        await using var harness = new TestHarness();
        harness.RespondQuery(RegXId, "[]");   // 内层 values 为空：某项本次无值

        var result = await harness.Reader.QueryItemAsync(TestHarness.Device, DataItemRequest.At(TestHarness.RegX));

        // 这不是错误：设备成功受理了，只是该项本次没有值（例如无报警）
        Assert.True(result.Succeeded);
        Assert.Equal(0, result.Count);
    }

    [Fact]
    public async Task Query_NoResultsAtAll_ShouldThrowDeviceUnavailable()
    {
        await using var harness = new TestHarness();

        // 外层 values 为空：设备回了 OK 却一个数据项都没有 → 视为设备不可用
        harness.Respond(harness.QueryTopic, request =>
            $$"""{"@id":"{{TestHarness.CorrelationIdOf(request)}}","values":[]}""");

        await Assert.ThrowsAsync<NcLinkDeviceUnavailableException>(() =>
            harness.Reader.QueryItemAsync(TestHarness.Device, DataItemRequest.At(TestHarness.RegX)));
    }

    [Fact]
    public async Task Query_NoResponse_ShouldThrowTimeout()
    {
        await using var harness = new TestHarness();

        // 先让设备"出现过"：只回应一次，之后不再回应。
        // 这样才会走"超时"分支，而不是"设备从未出现"分支。
        harness.Respond(harness.QueryTopic, request =>
            TestHarness.QueryResponse(RegXId, "[1]", TestHarness.CorrelationIdOf(request)));

        await harness.Reader.QueryItemAsync(TestHarness.Device, DataItemRequest.At(TestHarness.RegX));

        // 换掉响应：之后发出的请求没人应答
        harness.RespondSilently(harness.QueryTopic);

        var exception = await Assert.ThrowsAsync<NcLinkTimeoutException>(() =>
            harness.Reader.QueryItemAsync(TestHarness.Device, DataItemRequest.At(TestHarness.RegX)));

        Assert.Contains(TestHarness.Device, exception.Topic);
    }

    [Fact]
    public async Task Query_DeviceNeverSeen_ShouldThrowDeviceUnavailable()
    {
        await using var harness = new TestHarness();
        // 不注册任何响应：设备在本会话从未出现过

        var exception = await Assert.ThrowsAsync<NcLinkDeviceUnavailableException>(() =>
            harness.Reader.QueryItemAsync(TestHarness.Device, DataItemRequest.At(TestHarness.RegX)));

        Assert.Equal(TestHarness.Device, exception.DeviceId);
    }

    [Fact]
    public async Task Query_NgCode_ShouldMarkResultFailed()
    {
        await using var harness = new TestHarness();
        harness.Respond(harness.QueryTopic, request =>
            $$"""
              {"@id":"{{TestHarness.CorrelationIdOf(request)}}","values":[
                {"id":"{{RegXId}}","code":"NG","reason":"轴未使能"}]}
              """);

        var result = await harness.Reader.QueryItemAsync(TestHarness.Device, DataItemRequest.At(TestHarness.RegX));

        Assert.False(result.Succeeded);
        Assert.Equal("轴未使能", result.Reason);
    }

    #region 设置

    [Fact]
    public async Task SetItem_ShouldWriteValueWithIndexAndBitOffset()
    {
        await using var harness = new TestHarness();
        harness.RespondSetOk(RegXId);

        var ok = await harness.Writer.SetItemAsync(TestHarness.Device,
            DataItemRequest.Set(TestHarness.RegX, 1, index: 3, bitOffset: 2));

        Assert.True(ok);

        var parameters = harness.LastSet!.Values![0].Params!;
        Assert.Equal(3, parameters.Index);
        Assert.Equal(2, parameters.Offset);          // 位偏移走 offset 字段
        Assert.Equal(1, parameters.Value!.Value.GetInt32());
        Assert.Null(parameters.Operation);           // 未指定则不发 operation，由设备按场景取默认
    }

    [Fact]
    public async Task SetItem_WithExplicitOperation_ShouldSerializeWireName()
    {
        await using var harness = new TestHarness();
        harness.RespondSetOk(RegXId);

        await harness.Writer.SetItemAsync(TestHarness.Device, new DataItemRequest
        {
            Path = TestHarness.RegX,
            Operation = NcLinkOperation.SetValue,
            Keys = ["O0001"],
            Value = "00ff"
        });

        var parameters = harness.LastSet!.Values![0].Params!;
        Assert.Equal("set_value", parameters.Operation);
        Assert.Equal("O0001", parameters.Key);
    }

    [Fact]
    public async Task SetItem_DeviceReturnsNg_ShouldReportFalse()
    {
        await using var harness = new TestHarness();
        harness.RespondSetNg(RegXId);

        Assert.False(await harness.Writer.SetItemAsync(TestHarness.Device,
            DataItemRequest.Set(TestHarness.RegX, 0, index: 0)));
    }

    [Fact]
    public async Task Set_MultipleItems_ShouldReturnOneResultPerItem()
    {
        await using var harness = new TestHarness();
        var statusId = harness.Model.GetRequiredId("/MACHINE/STATUS");

        harness.Respond(harness.SetTopic, request =>
            $$"""
              {"@id":"{{TestHarness.CorrelationIdOf(request)}}","results":[
                {"id":"{{RegXId}}","code":"OK"},
                {"id":"{{statusId}}","code":"NG","reason":"只读"}]}
              """);

        var results = await harness.Writer.SetAsync(TestHarness.Device,
        [
            DataItemRequest.Set(TestHarness.RegX, 1),
            DataItemRequest.Set("/MACHINE/STATUS", 2)
        ]);

        Assert.Equal(2, results.Count);
        Assert.True(results[0].Succeeded);
        Assert.False(results[1].Succeeded);
        Assert.Equal("只读", results[1].Reason);
    }

    [Fact]
    public async Task Set_StringValue_ShouldBeJsonEncoded()
    {
        await using var harness = new TestHarness();
        harness.RespondSetOk(RegXId);

        await harness.Writer.SetItemAsync(TestHarness.Device, DataItemRequest.Set(TestHarness.RegX, "ABC"));

        Assert.Equal("ABC", harness.LastSet!.Values![0].Params!.Value!.Value.GetString());
    }

    #region 设置参数只能表达单个 index / 单个 key

    [Fact]
    public async Task Set_RangeIndex_ShouldBeRejectedInsteadOfSilentlyTruncated()
    {
        // 回归：BuildSetParams 曾写 `Indexes is { Count: 1 } ? Indexes[0].Start : null`。
        // 区间 [0,9] 的 Count 也是 1，于是只发 Start —— 用户以为写了 10 个寄存器，
        // 实际只写了 1 个，而且返回成功。协议里设置只能带单个 index，必须显式拒绝。
        await using var harness = new TestHarness();

        var ex = await Assert.ThrowsAsync<NcLinkInvalidRequestException>(
            () => harness.Writer.SetItemAsync(
                TestHarness.Device, DataItemRequest.At(TestHarness.RegX, 0, 9)));

        Assert.Contains("单个单点索引", ex.Message, StringComparison.Ordinal);
        Assert.Null(harness.LastSet);
    }

    [Fact]
    public async Task Set_MultipleIndexes_ShouldBeRejected()
    {
        await using var harness = new TestHarness();

        await Assert.ThrowsAsync<NcLinkInvalidRequestException>(
            () => harness.Writer.SetItemAsync(
                TestHarness.Device, DataItemRequest.At(TestHarness.RegX, 0, 1, 2)));

        Assert.Null(harness.LastSet);
    }

    [Fact]
    public async Task Set_IndexAndKeyTogether_ShouldBeRejected()
    {
        // 厂家 SetParams.isValid()：index 与 key 不能同时出现
        await using var harness = new TestHarness();

        await Assert.ThrowsAsync<NcLinkInvalidRequestException>(
            () => harness.Writer.SetItemAsync(TestHarness.Device, new DataItemRequest
            {
                Path = TestHarness.RegX,
                Indexes = [IndexRange.Single(0)],
                Keys = ["a", "b"],
                Value = 1
            }));

        Assert.Null(harness.LastSet);
    }

    [Fact]
    public async Task Set_ShouldCarryLengthInParams()
    {
        // 厂家 SetParams 有 length 字段；文件下发每块都要带它
        await using var harness = new TestHarness();
        harness.RespondSetOk(RegXId);

        await harness.Writer.SetItemAsync(TestHarness.Device, new DataItemRequest
        {
            Path = TestHarness.RegX,
            Value = "AA",
            Offset = 0,
            Length = 1
        });

        Assert.Equal(1, harness.LastSet!.Values![0].Params!.Length);
    }

    #endregion
}
    #endregion

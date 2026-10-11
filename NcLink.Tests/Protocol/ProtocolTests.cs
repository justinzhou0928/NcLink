using NcLink.Protocol;

namespace NcLink.Tests.Protocol;

public sealed class NcLinkTopicsTests
{
    private readonly NcLinkTopics _topics = NcLinkTopics.Default;

    [Fact]
    public void TopicPatterns_ShouldMatchOfficialConstants()
    {
        // 这些前缀来自官方核心库 cn.nerc.nclink.core.general.Topic
        Assert.Equal("Query/Request/D1", _topics.QueryRequest("D1"));
        Assert.Equal("Query/Response/D1", _topics.QueryResponse("D1"));
        Assert.Equal("Set/Request/D1", _topics.SetRequest("D1"));
        Assert.Equal("Set/Response/D1", _topics.SetResponse("D1"));
        Assert.Equal("Probe/Query/Request/D1", _topics.ProbeQueryRequest("D1"));
        Assert.Equal("Probe/Query/Response/D1", _topics.ProbeQueryResponse("D1"));
        Assert.Equal("Probe/Set/Request/D1", _topics.ProbeSetRequest("D1"));
        Assert.Equal("Probe/Set/Response/D1", _topics.ProbeSetResponse("D1"));
        Assert.Equal("Probe/Version/D1", _topics.ProbeVersion("D1"));
        // 采样主题含设备 SN（官方实测确认）——曾经错写成 Sample/{channelId}
        Assert.Equal("Sample/D1/ch0", _topics.Sample("D1", "ch0"));
        Assert.Equal("Register/Request", _topics.RegisterRequest);
        Assert.Equal("Register/Response/D1", _topics.RegisterResponse("D1"));
        Assert.Equal("Probe/Version/Response/D1", _topics.ProbeVersionResponse("D1"));
    }

    [Fact]
    public void AllResponseFilters_ShouldNotContainSampleWildcard()
    {
        // 采样按通道逐个显式订阅。若这里再加一条通配，同一帧会命中两个订阅，
        // MQTT 各投递一次 → 每帧收到两次（实测官方 4 帧 / 我们 8 帧）。
        Assert.DoesNotContain("Sample/+", _topics.AllResponseFilters);
        Assert.DoesNotContain("Sample/#", _topics.AllResponseFilters);
    }

    [Fact]
    public void TopicFor_EmptyDeviceId_ShouldThrow()
        => Assert.Throws<ArgumentException>(() => _topics.QueryRequest(""));

    [Theory]
    [InlineData("Query/Request/D1", "Query/Response/D1")]
    [InlineData("Set/Request/D1", "Set/Response/D1")]
    [InlineData("Probe/Query/Request/D1", "Probe/Query/Response/D1")]
    [InlineData("Probe/Set/Request/D1", "Probe/Set/Response/D1")]
    public void ResponseTopicFor_ShouldMapRequestToResponse(string request, string expected)
        => Assert.Equal(expected, _topics.ResponseTopicFor(request, "D1"));

    [Fact]
    public void ResponseTopicFor_UnknownRequest_ShouldReturnNull()
        => Assert.Null(_topics.ResponseTopicFor("Sample/ch0", "D1"));

    [Fact]
    public void IsResponseFor_ShouldAcceptMatchingDevice()
        => Assert.True(_topics.IsResponseFor("Query/Response/D1", "Query/Request/D1", "D1"));

    [Fact]
    public void IsResponseFor_ShouldRejectOtherDevice()
        => Assert.False(_topics.IsResponseFor("Query/Response/D2", "Query/Request/D1", "D1"));

    [Fact]
    public void IsResponseFor_ShouldRejectDifferentKind()
        => Assert.False(_topics.IsResponseFor("Set/Response/D1", "Query/Request/D1", "D1"));

    [Fact]
    public void IsResponseFor_ProbeSetShouldNotBeConfusedWithSet()
    {
        // 前缀判断顺序若写错，"Probe/Set/Request/" 会被 "Set/Request/" 抢先匹配
        Assert.True(_topics.IsResponseFor("Probe/Set/Response/D1", "Probe/Set/Request/D1", "D1"));
        Assert.False(_topics.IsResponseFor("Set/Response/D1", "Probe/Set/Request/D1", "D1"));
    }

    [Fact]
    public void IsResponseFor_ProbeQueryShouldNotBeConfusedWithQuery()
    {
        Assert.True(_topics.IsResponseFor("Probe/Query/Response/D1", "Probe/Query/Request/D1", "D1"));
        Assert.False(_topics.IsResponseFor("Query/Response/D1", "Probe/Query/Request/D1", "D1"));
    }

    [Fact]
    public void AllResponseFilters_ShouldCoverAllResponseKinds()
    {
        Assert.Contains("Query/Response/+", _topics.AllResponseFilters);
        Assert.Contains("Set/Response/+", _topics.AllResponseFilters);
        Assert.Contains("Probe/Query/Response/+", _topics.AllResponseFilters);
        Assert.Contains("Probe/Set/Response/+", _topics.AllResponseFilters);
        Assert.Contains("Register/Response/+", _topics.AllResponseFilters);
        Assert.Contains("Probe/Version/Response/+", _topics.AllResponseFilters);
    }
}

public sealed class JsonCodecTests
{
    private readonly JsonNcLinkCodec _codec = JsonNcLinkCodec.Default;

    [Fact]
    public void Encode_ShouldSerializeDerivedTypeMembers()
    {
        // 若按基类 NcLinkEnvelope 序列化，会丢掉派生字段——这是必须防住的回归
        var message = new QueryRequestMessage
        {
            CorrelationId = "abc",
            Ids = [new QueryRequestItemMessage { Id = "01035430", Params = new QueryParamsMessage { Indexes = ["0-9"] } }]
        };

        var json = System.Text.Encoding.UTF8.GetString(_codec.Encode(message));

        Assert.Contains("\"@id\":\"abc\"", json);
        Assert.Contains("\"ids\"", json);
        Assert.Contains("\"01035430\"", json);
        Assert.Contains("\"0-9\"", json);
    }

    [Fact]
    public void Encode_ShouldOmitNullMembers()
    {
        var json = System.Text.Encoding.UTF8.GetString(_codec.Encode(new ProbeQueryRequestMessage()));

        Assert.DoesNotContain("null", json);
    }

    [Fact]
    public void Encode_ShouldUseLowerCaseDeviceidForRegister()
    {
        // 注册报文用 "deviceid"（全小写），与其它报文的 "@id" 不同
        var json = System.Text.Encoding.UTF8.GetString(
            _codec.Encode(new RegisterRequestMessage { DeviceId = "SN1" }));

        Assert.Contains("\"deviceid\":\"SN1\"", json);
        Assert.DoesNotContain("@id", json);
    }

    [Fact]
    public void Decode_QueryResponse_ShouldReadValues()
    {
        const string json = """
        {"values":[{"id":"01035430","code":"OK","params":{"indexes":["0-9"]},
        "values":[0,3,6,9]}],"@id":"x1"}
        """;

        var message = _codec.Decode<QueryResponseMessage>(System.Text.Encoding.UTF8.GetBytes(json));

        Assert.NotNull(message);
        Assert.Equal("x1", message!.CorrelationId);

        var item = Assert.Single(message.Values!);
        Assert.Equal("01035430", item.Id);
        Assert.True(item.IsOk);
        Assert.Equal([0, 3, 6, 9], item.Values!.Select(v => v.GetInt32()));
    }

    [Fact]
    public void Decode_QueryResponseWithNg_ShouldExposeReason()
    {
        const string json = """
        {"values":[{"id":"01035408","code":"NG","reason":"file not found"}]}
        """;

        var message = _codec.Decode<QueryResponseMessage>(System.Text.Encoding.UTF8.GetBytes(json));
        var item = Assert.Single(message!.Values!);

        Assert.False(item.IsOk);
        Assert.Equal("file not found", item.Reason);
    }

    [Fact]
    public void Decode_Sample_ShouldHandleMixedValueTypes()
    {
        const string json = """
        {"id":"sample_channel0","beginTime":123,
         "data":[{"id":"010302","data":[1]},
                 {"id":"01035412","data":[]},
                 {"id":"01035001","data":["X"]}]}
        """;

        var message = _codec.Decode<SampleMessage>(System.Text.Encoding.UTF8.GetBytes(json));

        Assert.NotNull(message);
        Assert.Equal("sample_channel0", message!.Id);
        Assert.Equal(123, message.BeginTime);
        Assert.Equal(3, message.Data!.Count);
        Assert.Empty(message.Data[1].Data!);          // 空数组是有效值（例如无报警）
        Assert.Equal("X", message.Data[2].Data![0].GetString());
    }

    [Fact]
    public void Decode_ProbeResponse_ShouldExposeProbeObject()
    {
        // 报文格式来自对官方模拟器的实测抓包
        // （docs/official-simulator-verified-protocol.md）：
        // 模型嵌在 probe 对象里，code 是小写 ok
        const string json = """
            {"code":"ok","probe":{"id":"01","type":"NC_LINK_ROOT","devices":[{"id":"0103"}]}}
            """;

        var message = _codec.Decode<ProbeQueryResponseMessage>(System.Text.Encoding.UTF8.GetBytes(json));

        Assert.NotNull(message);
        Assert.Equal("ok", message!.Code);
        Assert.Equal(System.Text.Json.JsonValueKind.Object, message.Probe.ValueKind);
        Assert.Equal("01", message.Probe.GetProperty("id").GetString());
        Assert.Equal("0103", message.Probe.GetProperty("devices")[0].GetProperty("id").GetString());
    }

    [Fact]
    public void Decode_ProbeResponse_OfficialSample_ShouldCarryModelTree()
    {
        // 取实测响应的一段真实片段，确认嵌套结构能逐层读出
        const string json = """
            {"code":"ok","probe":{"id":"01","type":"NC_LINK_ROOT","name":"机床模型文件",
             "devices":[{"id":"0103","type":"MACHINE","version":"1.2",
              "configs":[{"id":"sample_channel0","type":"SAMPLE_CHANNEL","sampleInterval":3000,
                          "ids":[{"id":"010302"}]}]}]}}
            """;

        var message = _codec.Decode<ProbeQueryResponseMessage>(System.Text.Encoding.UTF8.GetBytes(json));

        var device = message!.Probe.GetProperty("devices")[0];
        var channel = device.GetProperty("configs")[0];

        Assert.Equal("MACHINE", device.GetProperty("type").GetString());
        Assert.Equal("sample_channel0", channel.GetProperty("id").GetString());
        Assert.Equal(3000, channel.GetProperty("sampleInterval").GetInt32());
        Assert.Equal("010302", channel.GetProperty("ids")[0].GetProperty("id").GetString());
    }

    [Fact]
    public void Decode_InvalidJson_ShouldReturnNull()
    {
        Assert.Null(_codec.Decode<QueryResponseMessage>("not json"u8.ToArray()));
    }

    [Fact]
    public void Decode_EmptyPayload_ShouldReturnNull()
        => Assert.Null(_codec.Decode<QueryResponseMessage>([]));
}

using NcLink.Client;
using NcLink.Client.Devices;

namespace NcLink.Tests.Model;

/// <summary>
/// 设备模型与路径解析测试。
/// 这些是纯逻辑，不需要 Broker —— 正是分层后可测性的体现。
/// </summary>
public sealed class DeviceModelTests
{
    private readonly DeviceModel _model = TestModel.Parse();

    [Theory]
    // 完整路径
    [InlineData("/NC_LINK_ROOT/MACHINE/CONTROLLER/VARIABLE@REG_X", "01035430")]
    // 去掉根前缀
    [InlineData("/MACHINE/CONTROLLER/VARIABLE@REG_X", "01035430")]
    // 不带前导斜杠
    [InlineData("MACHINE/CONTROLLER/VARIABLE@REG_X", "01035430")]
    // 直接用数据项 id
    [InlineData("01035430", "01035430")]
    // 只用标识
    [InlineData("VARIABLE@REG_X", "01035430")]
    // 普通数据项
    [InlineData("/MACHINE/STATUS", "010302")]
    [InlineData("MACHINE/STATUS", "010302")]
    // 无 number 的节点可用 type 作为标识
    [InlineData("STATUS", "010302")]
    // 配置节点
    [InlineData("/MACHINE/CONTROLLER/PARAMETER", "01035407")]
    public void Resolve_ShouldReturnExpectedId(string path, string expectedId)
    {
        Assert.Equal(expectedId, _model.GetRequiredId(path));
    }

    [Fact]
    public void Resolve_UnknownPath_ShouldThrow()
    {
        var exception = Assert.Throws<NcLinkModelPathNotFoundException>(
            () => _model.GetRequiredId("/MACHINE/DOES_NOT_EXIST"));

        Assert.Equal("/MACHINE/DOES_NOT_EXIST", exception.Path);
        Assert.Equal(TestModel.DeviceId, exception.DeviceId);
    }

    [Fact]
    public void FindId_UnknownPath_ShouldReturnNull()
        => Assert.Null(_model.FindId("/MACHINE/DOES_NOT_EXIST"));

    [Fact]
    public void FullPath_ShouldMatchOfficialPrefix()
    {
        var node = _model.FindByTag("VARIABLE@REG_X");

        Assert.NotNull(node);
        Assert.Equal("/NC_LINK_ROOT/MACHINE/CONTROLLER/VARIABLE@REG_X", node!.FullPath);
        Assert.Equal("MACHINE/CONTROLLER/VARIABLE@REG_X", node.Path);
    }

    [Fact]
    public void SampleChannel_ShouldBeIndexedWithIntervals()
    {
        var channel = _model.FindSampleChannel("sample_channel0");

        Assert.NotNull(channel);
        Assert.Equal("sample_channel0", channel!.ChannelId);
        Assert.Equal(TimeSpan.FromSeconds(1), channel.SampleInterval);
        Assert.Equal(["010302", "01035430"], channel.ItemIds);
    }

    [Fact]
    public void Settable_ShouldComeFromModelFile()
    {
        Assert.True(_model.FindByTag("VARIABLE@REG_X")!.Settable);
        Assert.True(_model.FindByTag("PARAMETER")!.Settable);
        Assert.False(_model.FindByTag("STATUS")!.Settable);
    }

    [Fact]
    public void Devices_ShouldExposeMachine()
    {
        var device = Assert.Single(_model.Devices);
        Assert.Equal("MACHINE", device.Type);
    }

    [Fact]
    public void Nodes_ShouldBeEnumeratedDepthFirst()
    {
        var machine = Assert.Single(_model.Devices);
        var all = machine.DescendantsAndSelf().ToList();

        Assert.Contains(all, n => n.Id == "01035430");   // 深层数据项
        Assert.Contains(all, n => n.Id == "sample_channel0");
    }
}

/// <summary>索引区间值对象测试。</summary>
public sealed class IndexRangeTests
{
    [Theory]
    [InlineData("13", 13, null)]
    [InlineData("0-9", 0, 9)]
    [InlineData("19-25", 19, 25)]
    [InlineData(" 7 ", 7, null)]
    public void Parse_ShouldHandleProtocolSyntax(string text, int start, int? end)
    {
        var range = IndexRange.Parse(text);

        Assert.Equal(start, range.Start);
        Assert.Equal(end, range.End);
    }

    [Theory]
    [InlineData("13", "13")]
    [InlineData("0-9", "0-9")]
    public void ToString_ShouldRoundTrip(string text, string expected)
        => Assert.Equal(expected, IndexRange.Parse(text).ToString());

    [Fact]
    public void Count_ShouldReflectInclusiveRange()
    {
        Assert.Equal(1, IndexRange.Parse("5").Count);
        Assert.Equal(10, IndexRange.Parse("0-9").Count);
    }

    [Fact]
    public void Enumerate_ShouldBeInclusive()
        => Assert.Equal([0, 1, 2, 3], IndexRange.Parse("0-3").Enumerate());

    [Fact]
    public void Ctor_ReversedRange_ShouldThrow()
        => Assert.Throws<ArgumentException>(() => new IndexRange(9, 0));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_Empty_ShouldThrow(string text)
        => Assert.Throws<ArgumentException>(() => IndexRange.Parse(text));
}

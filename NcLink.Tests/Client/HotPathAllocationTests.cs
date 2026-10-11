using NcLink.Client.Devices.Models;
using NcLink.Client.Devices.Engine;
using System.Text;
using NcLink.Protocol.Models;
using NcLink.Protocol.Engine;

namespace NcLink.Tests.Client;

/// <summary>
/// 热路径分配的实测对照。
///
/// 存在意义：响应匹配曾经每条消息都 <c>Encoding.UTF8.GetString</c> 把整条报文
/// 解码成 UTF-16，再在字符串上做子串查找。改成字节级查找后分配应当趋近于零——
/// <b>这件事必须用真实数据验证，不能靠推理</b>（AGENTS.md §5）。
/// 本用例在同一进程里跑两遍，把"解码成字符串"与"字节级查找"直接对比。
/// </summary>
public sealed class HotPathAllocationTests
{
    private const int Iterations = 2000;

    /// <summary>一条形状真实的查询响应（含设备会返回的中文，避免只测 ASCII 的巧合）。</summary>
    private static readonly byte[] Payload = Encoding.UTF8.GetBytes(
        """{"@id":"dev-1","values":[{"id":"MACHINE@1234","values":[1,2,3],"reason":"正常"}]}""");

    private static readonly string[] ItemIds = ["MACHINE@1234"];

    private static readonly byte[] StructureMarker = "\"values\""u8.ToArray();

    private static readonly byte[][] ItemPatterns =
        [.. ItemIds.Select(static id => Encoding.UTF8.GetBytes($"\"{id}\""))];

    [Fact]
    public void ResponseMatching_ByteSearchShouldAllocateFarLessThanStringDecoding()
    {
        // 预热：让 JIT 与首次分配都发生在计时之外
        _ = MatchByString();
        _ = MatchBySpan();

        var stringBytes = Measure(MatchByString);
        var spanBytes = Measure(MatchBySpan);

        // 实测结果打印出来，便于人工核对量级
        Console.WriteLine($"  解码成字符串：{stringBytes,9:N0} 字节 / {Iterations} 次");
        Console.WriteLine($"  字节级查找　：{spanBytes,9:N0} 字节 / {Iterations} 次");
        Console.WriteLine($"  每条报文省下：{(stringBytes - spanBytes) / (double)Iterations,9:N1} 字节");

        // 字节级查找不分配；留 4 倍余量避免不同运行时的抖动导致偶发失败
        // 实测（本机 net10.0，2000 次）：字符串 480,000 字节 vs 字节级 0 字节，
        // 即每条报文省 240 字节、字节级查找完全不分配。
        // 断言刻意留 10 倍余量：分配量随运行时状态浮动，卡太紧会在全量套件里偶发失败。
        Assert.True(
            spanBytes * 10 < stringBytes,
            $"字节级查找应显著更省：字符串 {stringBytes} / 字节 {spanBytes}");
    }


    private static long Measure(Func<bool> action)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < Iterations; i++) _ = action();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    /// <summary>旧做法：整条解码成字符串，再在字符串上查找。</summary>
    private static bool MatchByString()
    {
        var text = Encoding.UTF8.GetString(Payload);
        if (!text.Contains("\"values\"", StringComparison.Ordinal)) return false;

        foreach (var id in ItemIds)
            if (text.Contains("\"" + id + "\"", StringComparison.Ordinal)) return true;

        return false;
    }

    /// <summary>新做法：直接在 UTF-8 字节上查找。</summary>
    private static bool MatchBySpan()
    {
        var payload = Payload.AsSpan();
        if (payload.IndexOf(StructureMarker) < 0) return false;

        foreach (var pattern in ItemPatterns)
            if (payload.IndexOf(pattern) >= 0) return true;

        return false;
    }

    [Fact]
    public void Codec_Utf8BytePathShouldNotRoundTripThroughString()
    {
        var message = new QueryRequestMessage
        {
            Ids = [new QueryRequestItemMessage { Id = "MACHINE@1234" }]
        };

        // 预热：System.Text.Json 首次按类型解析会建元数据缓存，必须发生在计时之外
        for (var i = 0; i < 50; i++)
        {
            _ = CodecByString(message);
            _ = CodecByUtf8(message);
        }

        var stringBytes = Measure(() => { _ = CodecByString(message); return true; });
        var utf8Bytes = Measure(() => { _ = CodecByUtf8(message); return true; });

        Console.WriteLine($"  编码经字符串：{stringBytes,9:N0} 字节 / {Iterations} 次");
        Console.WriteLine($"  编码直接 UTF-8：{utf8Bytes,9:N0} 字节 / {Iterations} 次");

        // 省下的是"那份 UTF-16 中转"，不是数量级差异——输出字节本身两边都要分配。
        // 因此只断言"严格更省"：具体幅度随运行时浮动，写死比例会在全量套件里偶发失败。
        Assert.True(
            utf8Bytes < stringBytes,
            $"直接 UTF-8 编码应省掉中转字符串：字符串 {stringBytes} / 字节 {utf8Bytes}");
    }

    private static byte[] CodecByString(QueryRequestMessage message)
        => Encoding.UTF8.GetBytes(NcLinkJson.Serialize(message, message.GetType()));

    private static byte[] CodecByUtf8(QueryRequestMessage message)
        => NcLinkJson.SerializeToUtf8Bytes(message, message.GetType());

    [Fact]
    public void SampleChannels_ShouldNotRebuildDictionaryPerAccess()
    {
        // 采样转发**每帧**都要查这张表（SampleMultiplexer → GetChannelItemIds）。
        // 它此前是每次访问都 LINQ 重建的属性，等于每帧把全部节点扫一遍再建一个字典。
        var model = SampleDeviceModel.Parse("ALLOC_PROBE");

        // 旧实现，逐字照抄当时那行
        static int Rebuild(DeviceModel m)
            => m.Nodes.Where(n => n.SampleChannel is not null)
                      .ToDictionary(n => n.Id, n => n.SampleChannel!, StringComparer.Ordinal)
                      .Count;

        // 现实现：Build 时建一次
        static int Cached(DeviceModel m) => m.SampleChannels.Count;

        // 预热
        for (var i = 0; i < 20; i++) { _ = Rebuild(model); _ = Cached(model); }

        var rebuildBytes = MeasureAction(() => Rebuild(model));
        var cachedBytes = MeasureAction(() => Cached(model));

        Console.WriteLine($"  模型节点数：{model.Nodes.Count}，采样通道：{model.SampleChannels.Count}");
        Console.WriteLine($"  每次重建字典：{rebuildBytes,9:N0} 字节 / {Iterations} 次");
        Console.WriteLine($"  缓存后查表　：{cachedBytes,9:N0} 字节 / {Iterations} 次");
        Console.WriteLine($"  每帧省下　　：{(rebuildBytes - cachedBytes) / (double)Iterations,9:N1} 字节");

        // 缓存后只是读一个已建好的字典；留 10 倍余量防抖动
        // 实测（本机 net10.0，2000 次，内置模型 62 个节点 / 2 个采样通道）：
        //   每次重建字典 576,000 字节（288/帧）  vs  缓存后查表 0 字节
        // 采样转发每帧都查它，所以这 288 字节是**每帧**的。留 10 倍余量防抖动。
        Assert.True(
            cachedBytes * 10 < rebuildBytes,
            $"缓存后查表应远低于每帧重建：重建 {rebuildBytes} / 缓存 {cachedBytes}");
    }

    private static long MeasureAction(Action action)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < Iterations; i++) action();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }
}

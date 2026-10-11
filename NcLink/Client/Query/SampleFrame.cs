using System.Text.Json;

namespace NcLink.Client.Query;

/// <summary>
/// 一帧采样数据。对应主题 <c>Sample/{deviceId}/{channelId}</c>。
///
/// 报文形状经官方模拟器实测确认（见
/// <c>docs/official-simulator-verified-protocol.md</c>）：
/// <code>
/// { "id":"sample_channel0", "beginTime":"1791349104216",
///   "data":[ {"data":[1]}, {"data":[0]}, {"data":[100]}, {"data":[0]},
///            {"data":[[{"number":"01020340","text":"PLC报警"}]]} ] }
/// </code>
/// <b>三个要点</b>：
/// <list type="number">
/// <item><c>data</c> 可以是 <c>null</c>（该通道本次无数据）；</item>
/// <item><c>data[]</c> 的每个元素<b>只有 <c>data</c>、没有 <c>id</c></b>，
///   因此无法按 id 索引——只能按<b>位置</b>与通道配置里的 <c>ids</c> 顺序对应
///   （见 <see cref="ItemIds"/>）；</item>
/// <item>值可以是嵌套数组/对象（如报警项 <c>[[{number,text}]]</c>），
///   所以每项的值是 <see cref="JsonElement"/> 列表，不做强制解包。</item>
/// </list>
/// </summary>
public sealed class SampleFrame
{
    public SampleFrame(
        string deviceId,
        string channelId,
        long? beginTime,
        IReadOnlyList<SampleItem> items,
        IReadOnlyList<string>? itemIds = null,
        DateTimeOffset? receivedAt = null)
    {
        DeviceId = deviceId;
        ChannelId = channelId;
        BeginTime = beginTime;
        Items = items;
        ItemIds = itemIds ?? [];
        // 由调用方给出（分发器持有 TimeProvider）。这里不读墙钟：
        // 一是模型不该依赖时钟服务，二是本地时间无法在单测里断言。
        ReceivedAt = receivedAt ?? DateTimeOffset.UtcNow;
    }

    /// <summary>数据所属设备 SN（取自主题）。</summary>
    public string DeviceId { get; }

    /// <summary>采样通道 id。</summary>
    public string ChannelId { get; }

    /// <summary>设备给出的采样起始时间戳（单位以现场为准）。</summary>
    public long? BeginTime { get; }

    /// <summary>
    /// 按<b>位置</b>排列的采样项，与通道配置 <c>ids</c> 的顺序一致。
    /// 报文本体不携带数据项 id，因此这里不提供 id 索引。
    /// </summary>
    public IReadOnlyList<SampleItem> Items { get; }

    /// <summary>
    /// 通道配置里声明的数据项 id（来自设备模型，顺序与 <see cref="Items"/> 对应）。
    ///
    /// 用于把位置映射回数据项：<c>Items[i]</c> 对应 <c>ItemIds[i]</c>。
    /// 若设备模型未提供该通道的 <c>ids</c>（或个数与 <see cref="Items"/> 不一致），
    /// 本数组可能为空——此时只能按位置自行处理。
    /// </summary>
    public IReadOnlyList<string> ItemIds { get; }

    /// <summary>本地收到该帧的时间。</summary>
    public DateTimeOffset ReceivedAt { get; }

    /// <summary>
    /// 取值：<c>Items[index]</c> 的值列表；越界返回空。
    /// 便捷方法，等价于 <c>Items[index].Data</c>。
    /// </summary>
    public IReadOnlyList<JsonElement> ValuesAt(int index)
        => index >= 0 && index < Items.Count ? Items[index].Data : [];

    /// <summary>
    /// 按数据项 id 取值；要求 <see cref="ItemIds"/> 与 <see cref="Items"/> 位置可对应。
    /// 找不到该 id 时返回空。
    /// </summary>
    public IReadOnlyList<JsonElement> ValuesOf(string itemId)
    {
        for (var i = 0; i < ItemIds.Count && i < Items.Count; i++)
            if (string.Equals(ItemIds[i], itemId, StringComparison.Ordinal))
                return Items[i].Data;
        return [];
    }
}

/// <summary>
/// 一个采样项。对应报文里 <c>data[]</c> 的一个元素。
///
/// <see cref="Id"/> 在官方设备实测的报文里<b>不存在</b>（元素只有 <c>data</c>），
/// 保留该字段是为了兼容可能携带 id 的其它实现。
/// </summary>
public sealed class SampleItem
{
    public SampleItem(string? id, string? encoding, IReadOnlyList<JsonElement> data)
    {
        Id = id;
        Encoding = encoding;
        Data = data;
    }

    /// <summary>数据项 id（官方设备实测不返回，通常为 null）。</summary>
    public string? Id { get; }

    /// <summary>编码方式；为空表示原始值。</summary>
    public string? Encoding { get; }

    /// <summary>该次采样的值列表；可为空表示本次无值。</summary>
    public IReadOnlyList<JsonElement> Data { get; }
}

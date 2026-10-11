namespace NcLink.Client.Devices.Models;

/// <summary>
/// 节点标识（协议里由 <c>type</c> 与 <c>number</c> 组成）。
/// </summary>
public sealed class NodeTag
{
    public NodeTag(string? type, string? number)
    {
        Type = string.IsNullOrWhiteSpace(type) ? null : type;
        Number = string.IsNullOrWhiteSpace(number) ? null : number;
        Segment = Number is null ? Type ?? string.Empty : $"{Type}@{Number}";
    }

    public string? Type { get; }

    /// <summary>协议里的 <c>number</c> 字段，如 <c>REG_X</c>。</summary>
    public string? Number { get; }

    /// <summary>形如 <c>VARIABLE@REG_X</c>；无 number 时退化为 type。</summary>
    public string Segment { get; }

    public override string ToString() => Segment;
}

/// <summary>
/// 设备模型中的一个节点（对外不可变）。路径与子节点在加载时一次算好。
/// </summary>
public sealed class ModelNode
{
    private IReadOnlyList<ModelNode> _children = [];

    internal ModelNode(DeviceModelRawNode raw, string path, SampleChannelInfo? sampleChannel)
    {
        Id = raw.Id!;
        Type = raw.Type;
        Name = raw.Name;
        Description = raw.Description;
        Tag = new NodeTag(raw.Type, raw.Number);
        DataType = raw.DataType;
        Settable = raw.Settable ?? false;
        Commanded = raw.Commanded ?? false;
        Path = path;
        SampleChannel = sampleChannel;
    }

    /// <summary>模型文件里的数据项 id，请求报文里用的就是它。</summary>
    public string Id { get; }

    /// <summary>节点类型，如 <c>MACHINE</c> / <c>CONTROLLER</c> / <c>VARIABLE</c>。</summary>
    public string? Type { get; }

    public string? Name { get; }

    public string? Description { get; }

    public NodeTag Tag { get; }

    /// <summary><c>LIST</c> / <c>HASH</c>；非数据项节点为 null。</summary>
    public string? DataType { get; }

    /// <summary>是否可写。</summary>
    public bool Settable { get; }

    /// <summary>是否为指令值（相对实际值）。</summary>
    public bool Commanded { get; }

    /// <summary>不含根前缀的规范路径，如 <c>MACHINE/CONTROLLER/VARIABLE@REG_X</c>。</summary>
    public string Path { get; }

    /// <summary>完整路径，如 <c>/NC_LINK_ROOT/MACHINE/CONTROLLER/VARIABLE@REG_X</c>。</summary>
    public string FullPath => "/" + DeviceModel.RootType + "/" + Path;

    public IReadOnlyList<ModelNode> Children => _children;

    /// <summary>仅采样通道节点非 null。</summary>
    public SampleChannelInfo? SampleChannel { get; }

    public bool IsSampleChannel => SampleChannel is not null;

    /// <summary>由 <see cref="DeviceModel.Build"/> 在父节点构造后回填子节点。</summary>
    internal void InitChildren(IReadOnlyList<ModelNode> children) => _children = children;

    /// <summary>深度优先枚举自身与全部后代。</summary>
    public IEnumerable<ModelNode> DescendantsAndSelf()
    {
        yield return this;
        foreach (var child in _children)
            foreach (var node in child.DescendantsAndSelf())
                yield return node;
    }

    public override string ToString() => $"[{Type}] {Id} {Name} ({FullPath})";
}

/// <summary>采样通道信息（模型里的 <c>SAMPLE_CHANNEL</c> 节点）。</summary>
public sealed class SampleChannelInfo
{
    internal SampleChannelInfo(
        string channelId,
        TimeSpan? sampleInterval,
        TimeSpan? uploadInterval,
        IReadOnlyList<string> itemIds)
    {
        ChannelId = channelId;
        SampleInterval = sampleInterval;
        UploadInterval = uploadInterval;
        ItemIds = itemIds;
    }

    /// <summary>通道 id，即 MQTT 主题 <c>Sample/{channelId}</c> 的后半段。</summary>
    public string ChannelId { get; }

    public TimeSpan? SampleInterval { get; }

    public TimeSpan? UploadInterval { get; }

    /// <summary>通道内包含的数据项 id 列表。</summary>
    public IReadOnlyList<string> ItemIds { get; }
}

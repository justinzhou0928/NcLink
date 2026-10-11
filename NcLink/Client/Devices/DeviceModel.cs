using NcLink.Client.Connection;

namespace NcLink.Client.Devices;

/// <summary>
/// 设备模型（不可变）。负责路径 / 标识 → 数据项 id 的解析。
///
/// 路径规则来自协议：层级用 <c>/</c> 分隔，数据项用 <c>@</c> 连接 type 与 number，
/// 例如 <c>/NC_LINK_ROOT/MACHINE/CONTROLLER/VARIABLE@REG_X</c>。
///
/// 加载时建立两个索引（路径、id）；标识按需扫描——采样通道只有几个，
/// 没必要再维护第三个索引。
/// </summary>
public sealed class DeviceModel
{
    public const string RootType = "NC_LINK_ROOT";
    public const string MachineType = "MACHINE";
    public const string SampleChannelType = "SAMPLE_CHANNEL";

    private readonly Dictionary<string, ModelNode> _byPath = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ModelNode> _byId = new(StringComparer.Ordinal);
    private readonly List<ModelNode> _nodes = [];
    private readonly List<ModelNode> _devices = [];
    private IReadOnlyDictionary<string, SampleChannelInfo> _sampleChannels =
        new Dictionary<string, SampleChannelInfo>(StringComparer.Ordinal);

    private DeviceModel(string deviceId) => DeviceId = deviceId;

    /// <summary>本模型所属设备的 SN。</summary>
    public string DeviceId { get; }

    /// <summary>
    /// 收到的模型文件原文（未加工）。
    ///
    /// 保留它是为了让"原样返回模型"的调用方（例如 apiServer 的 <c>GET /model</c>）
    /// 拿到的内容与设备发出的<b>逐字节一致</b>，而不是重新序列化后的近似结果。
    /// </summary>
    public string RawJson { get; private init; } = string.Empty;

    /// <summary>全部节点（深度优先，父节点在前）。</summary>
    public IReadOnlyList<ModelNode> Nodes => _nodes;

    /// <summary>顶层设备节点（通常是唯一的 <c>MACHINE</c>）。</summary>
    public IReadOnlyList<ModelNode> Devices => _devices;

    /// <summary>
    /// 采样通道，键为通道 id。
    ///
    /// 在 <see cref="Build"/> 时建好一次。它<b>不能</b>写成每次访问都
    /// <c>LINQ</c> 重建的属性——采样转发<b>每帧</b>都要查这张表
    /// （见 <c>SampleMultiplexer</c>），每秒上千帧时就是每秒重建上千个字典。
    /// </summary>
    public IReadOnlyDictionary<string, SampleChannelInfo> SampleChannels => _sampleChannels;

    #region 构建

    /// <summary>由模型文件内容构建，并建立路径索引。</summary>
    internal static DeviceModel Build(string deviceId, DeviceModelRaw raw, string rawJson = "")
    {
        var model = new DeviceModel(deviceId) { RawJson = rawJson };

        DeviceModelRawNode[] deviceSources = [.. raw.Devices ?? []];
        foreach (var deviceSource in deviceSources)
        {
            var node = Visit(deviceSource, parentPath: string.Empty);
            if (node is not null) model._devices.Add(node);
        }

        model._sampleChannels = model._nodes
            .Where(n => n.SampleChannel is not null)
            .ToDictionary(n => n.Id, n => n.SampleChannel!, StringComparer.Ordinal);

        return model;

        // 局部递归函数：索引收集走闭包，不再靠参数穿针
        ModelNode? Visit(DeviceModelRawNode source, string parentPath)
        {
            if (string.IsNullOrWhiteSpace(source.Id)) return null;

            var tag = new NodeTag(source.Type, source.Number);
            var path = string.IsNullOrEmpty(parentPath)
                ? tag.Segment
                : parentPath + "/" + tag.Segment;

            var node = new ModelNode(source, path, CreateChannelInfo(source));

            model._nodes.Add(node);
            model._byPath.TryAdd(path, node);
            model._byPath.TryAdd("/" + path, node);
            model._byId.TryAdd(node.Id, node);

            var children = new List<ModelNode>();
            foreach (var childSource in source.EnumerateChildren())
            {
                var child = Visit(childSource, path);
                if (child is not null) children.Add(child);
            }

            node.InitChildren(children);
            return node;
        }
    }

    private static SampleChannelInfo? CreateChannelInfo(DeviceModelRawNode source)
    {
        if (!string.Equals(source.Type, SampleChannelType, StringComparison.OrdinalIgnoreCase))
            return null;

        var itemIds = (source.Ids ?? [])
            .Select(r => r.Id)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .ToArray();

        return new SampleChannelInfo(
            channelId: source.Id!,
            sampleInterval: ToTimeSpan(source.SampleInterval),
            uploadInterval: ToTimeSpan(source.UploadInterval),
            itemIds: itemIds);
    }

    private static TimeSpan? ToTimeSpan(long? milliseconds)
        => milliseconds is > 0 ? TimeSpan.FromMilliseconds(milliseconds.Value) : null;

    #endregion

    #region 解析

    /// <summary>
    /// 解析路径、id 或标识，返回对应节点。以下写法都指向同一数据项：
    /// <list type="bullet">
    /// <item><c>/NC_LINK_ROOT/MACHINE/CONTROLLER/VARIABLE@REG_X</c></item>
    /// <item><c>/MACHINE/CONTROLLER/VARIABLE@REG_X</c></item>
    /// <item><c>MACHINE/CONTROLLER/VARIABLE@REG_X</c></item>
    /// <item><c>VARIABLE@REG_X</c>（标识）</item>
    /// <item><c>01035430</c>（id）</item>
    /// </list>
    /// </summary>
    public ModelNode? Resolve(string pathIdOrTag)
    {
        if (string.IsNullOrWhiteSpace(pathIdOrTag)) return null;

        var key = pathIdOrTag.Trim();

        // 路径（含带根前缀与不带两种写法）
        if (_byPath.TryGetValue(key, out var node)) return node;

        var trimmed = key.TrimStart('/');
        var rootPrefix = RootType + "/";
        if (trimmed.StartsWith(rootPrefix, StringComparison.Ordinal))
            trimmed = trimmed[rootPrefix.Length..];

        if (_byPath.TryGetValue(trimmed, out node)) return node;

        // id
        if (_byId.TryGetValue(key, out node)) return node;

        // 标识：按路径末段匹配（如 VARIABLE@REG_X）
        foreach (var candidate in _nodes)
        {
            if (!string.Equals(candidate.Tag.Segment, trimmed, StringComparison.Ordinal)) continue;
            return candidate;
        }

        // 最后退化：按名称唯一匹配
        return _nodes.FirstOrDefault(n => string.Equals(n.Name, key, StringComparison.Ordinal));
    }

    /// <summary>取数据项 id；解析失败返回 null。</summary>
    public string? FindId(string pathIdOrTag) => Resolve(pathIdOrTag)?.Id;

    /// <summary>取数据项 id；解析失败抛 <see cref="NcLinkModelPathNotFoundException"/>。</summary>
    public string GetRequiredId(string pathIdOrTag)
        => Resolve(pathIdOrTag)?.Id
           ?? throw new NcLinkModelPathNotFoundException(pathIdOrTag, DeviceId);

    /// <summary>按标识（如 <c>VARIABLE@REG_X</c> 或 <c>STATUS</c>）取节点。</summary>
    public ModelNode? FindByTag(string segment) => Resolve(segment);

    /// <summary>按通道 id 取采样通道信息。</summary>
    public SampleChannelInfo? FindSampleChannel(string channelId)
        => string.IsNullOrWhiteSpace(channelId) ? null
           : _nodes.FirstOrDefault(n => string.Equals(n.Id, channelId, StringComparison.Ordinal))
                   ?.SampleChannel;

    #endregion

}

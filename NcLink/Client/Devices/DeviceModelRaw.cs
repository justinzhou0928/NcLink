namespace NcLink.Client.Devices;

/// <summary>
/// 设备模型文件的原始结构，与 model.json 一一对应。
/// 它只是 JSON 的映射；业务代码请用 <see cref="DeviceModel"/> 的查询方法。
/// </summary>
public sealed class DeviceModelRaw
{
    public string? Id { get; set; }

    public string? Type { get; set; }

    public string? Name { get; set; }

    public List<DeviceModelRawNode>? Devices { get; set; }
}

/// <summary>模型文件中的节点（递归结构）。</summary>
public sealed class DeviceModelRawNode
{
    public string? Id { get; set; }

    public string? Type { get; set; }

    public string? Name { get; set; }

    public string? Description { get; set; }

    public string? Version { get; set; }

    /// <summary>标识，如 <c>REG_X</c>、<c>AXIS_0</c>、<c>X</c>。</summary>
    public string? Number { get; set; }

    /// <summary><c>LIST</c> / <c>HASH</c>。</summary>
    public string? DataType { get; set; }

    public bool? Settable { get; set; }

    public bool? Commanded { get; set; }

    /// <summary>采样周期（毫秒）。</summary>
    public long? SampleInterval { get; set; }

    /// <summary>上报周期（毫秒）。</summary>
    public long? UploadInterval { get; set; }

    /// <summary>采样通道成员引用，元素形如 <c>{"id":"010302"}</c>。</summary>
    public List<DeviceModelRawRef>? Ids { get; set; }

    public List<DeviceModelRawNode>? Devices { get; set; }

    public List<DeviceModelRawNode>? Components { get; set; }

    public List<DeviceModelRawNode>? DataItems { get; set; }

    public List<DeviceModelRawNode>? Configs { get; set; }

    /// <summary>按协议顺序枚举全部子节点（devices → components → dataItems → configs）。</summary>
    internal IEnumerable<DeviceModelRawNode> EnumerateChildren()
    {
        foreach (var list in new[] { Devices, Components, DataItems, Configs })
        {
            if (list is null) continue;
            foreach (var child in list)
                if (child is not null) yield return child;
        }
    }
}

/// <summary>采样通道成员引用。</summary>
public sealed class DeviceModelRawRef
{
    public string? Id { get; set; }
}

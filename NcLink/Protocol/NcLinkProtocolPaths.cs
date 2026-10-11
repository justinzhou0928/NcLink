namespace NcLink.Protocol;

/// <summary>
/// 协议里固定的数据项路径。
///
/// 与 <see cref="NcLinkTopicPrefixes"/>（主题前缀）、
/// <c>NcLinkOperationNames</c>（操作名）、<c>NcLinkCodeNames</c>（返回码）并列，
/// 都属于「设备侧的固定约定」——散落在各组件里一旦写错就是静默故障，
/// 因此集中到协议层。
///
/// 注意这些路径<b>不含根前缀</b>（<c>/NC_LINK_ROOT</c> 由
/// <c>DeviceModel.Resolve</c> 负责补齐）。
/// </summary>
public static class NcLinkProtocolPaths
{
    /// <summary>
    /// G 代码文件节点：以 <c>get_keys</c> 列出目录、
    /// <c>get_attributes</c> 取属性、<c>get_value</c>/<c>set_value</c> 读写内容。
    /// </summary>
    public const string File = "/MACHINE/CONTROLLER/FILE";
}

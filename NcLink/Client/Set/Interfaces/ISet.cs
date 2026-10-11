using NcLink.Client.Query.Models;
using NcLink.Client.Files.Interfaces;

namespace NcLink.Client.Set.Interfaces;

/// <summary>
/// 设置能力：按数据项写值、位操作。
///
/// 对应官方《应用开发指导手册》5.1「查询/设置相关参数」中的设置部分
/// （<c>set_value</c>，含 <c>offset</c> 位偏移）。
/// 文件的建目录/删除见 <c>IFileTransfer</c>。
/// </summary>
public interface ISet
{
    /// <summary>按数据项设置。</summary>
    Task<IReadOnlyList<SetItemResult>> SetAsync(
        string deviceId,
        IEnumerable<DataItemRequest> items,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default);

    /// <summary>设置单个数据项；返回设备是否受理成功。</summary>
    Task<bool> SetItemAsync(
        string deviceId,
        DataItemRequest item,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default);
}

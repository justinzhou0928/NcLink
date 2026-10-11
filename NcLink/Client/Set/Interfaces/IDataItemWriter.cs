using NcLink.Client.Query.Models;

namespace NcLink.Client.Set.Interfaces;

/// <summary>
/// 写数据项的最小操作面。
///
/// 文件传输用它下发文件内容（<c>set_value</c>）、建目录（<c>add</c>）、
/// 删除（<c>delete</c>），但不需要整个 <see cref="ISet"/>。
/// </summary>
internal interface IDataItemWriter
{
    Task<bool> SetItemAsync(
        string deviceId,
        DataItemRequest item,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default);
}

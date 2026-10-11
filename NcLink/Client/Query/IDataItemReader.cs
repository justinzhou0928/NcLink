using NcLink.Client.Query;

namespace NcLink.Client.Query;

/// <summary>
/// 读数据项的最小操作面。
///
/// 文件传输需要按 <c>offset</c> / <c>length</c> 分块读取文件内容，
/// 但不需要整个 <see cref="IQuery"/>。窄接口让依赖关系写在签名上，
/// 也让文件传输可以脱离完整客户端单独测试。
/// </summary>
internal interface IDataItemReader
{
    Task<DataItemResult> QueryItemAsync(
        string deviceId,
        DataItemRequest item,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default);
}

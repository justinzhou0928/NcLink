namespace NcLink.ApiServer.Interfaces;

/// <summary>
/// 服务端采样订阅的存取契约。
///
/// 采样是服务端特有的：设备是推送式的，缓存只为 HTTP 轮询而存在。
/// 控制器面向这个接口，实现落在 <c>Engine/SampleCache</c>（internal）——
/// 公开契约与内部实现分开，是仓库一贯的分层。
/// </summary>
public interface ISampleCache
{
    /// <summary>当前持有的订阅数（含尚未被回收的空闲条目）。</summary>
    int Count { get; }

    /// <summary>建立订阅，返回 <c>sub_id</c>（GUID 字符串）。</summary>
    Task<string> SubscribeAsync(string deviceId, string channelId, CancellationToken cancellationToken);

    /// <summary>
    /// 取走该 <c>sub_id</c> 已缓存的帧（drain 语义：取走即清空）。
    /// <c>sub_id</c> 不存在时返回 <c>null</c>——调用方据此返回 <c>code 17</c>。
    /// </summary>
    IReadOnlyList<object>? Drain(string subId);

    /// <summary>取消订阅；返回它此前是否存在。</summary>
    Task<bool> UnsubscribeAsync(string subId, CancellationToken cancellationToken);
}

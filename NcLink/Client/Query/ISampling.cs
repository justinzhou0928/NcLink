namespace NcLink.Client.Query;

/// <summary>
/// 采样订阅的生命周期管理：客户端释放时取消全部订阅。
///
/// 订阅的<b>建立</b>在 <see cref="IQuery"/> 上（<c>SubscribeSampleAsync</c> /
/// <c>SubscribeAllSamplesAsync</c>）；这里只负责收尾，
/// 供客户端在释放时调用，不对外暴露订阅入口。
///
/// 曾在这里暴露过 <c>DroppedAmbiguousFrames</c>（"通道 id 歧义丢帧数"），
/// 随采样主题实测结果一并移除——主题含设备 SN，归属本就确定，不存在歧义。
/// </summary>
internal interface ISampling
{
    /// <summary>取消全部采样订阅（客户端释放时调用）。</summary>
    Task UnsubscribeAllAsync();
}

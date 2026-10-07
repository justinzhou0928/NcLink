using NcLink.Client.Devices.Models;
using NcLink.Client.Query.Models;

namespace NcLink.Client.Query.Interfaces;

/// <summary>
/// 查询能力：探测/取设备模型、按数据项查询、采样订阅。
///
/// 对应官方《应用开发指导手册》5.1「查询/设置相关参数」中的查询部分
/// （<c>get_value</c> / <c>get_length</c> / <c>get_keys</c> / <c>get_attributes</c>），
/// 以及设备侧的探测与采样订阅。
/// </summary>
public interface IQuery
{
    #region 设备模型

    /// <summary>向设备发起探测，取回并缓存其模型。</summary>
    Task<DeviceModel> ProbeAsync(
        string deviceId,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default);

    /// <summary>取设备模型；默认命中缓存。</summary>
    Task<DeviceModel> GetModelAsync(
        string deviceId,
        bool refresh = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 注入一个已知模型，跳过探测。
    ///
    /// 用于模型由外部配置提供（而非从设备取回）的场景，
    /// 以及离线/离线联调时预置模型。
    /// </summary>
    void RegisterModel(DeviceModel model);

    #endregion

    #region 查询

    /// <summary>按数据项查询。</summary>
    Task<IReadOnlyList<DataItemResult>> QueryAsync(
        string deviceId,
        IEnumerable<DataItemRequest> items,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default);

    /// <summary>查询单个数据项。</summary>
    Task<DataItemResult> QueryItemAsync(
        string deviceId,
        DataItemRequest item,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default);

    #endregion

    #region 采样

    /// <summary>订阅一个采样通道。</summary>
    Task<ISampleSubscription> SubscribeSampleAsync(
        string deviceId,
        string channelId,
        CancellationToken cancellationToken = default);

    /// <summary>订阅设备模型中的全部采样通道。</summary>
    Task<IReadOnlyList<ISampleSubscription>> SubscribeAllSamplesAsync(
        string deviceId,
        CancellationToken cancellationToken = default);

    #endregion
}

using NcLink.Client.Connection.Engine;
using NcLink.Client.Connection.Exceptions;
using NcLink.Client.Devices.Engine;
using NcLink.Client.Devices.Models;
using NcLink.Client.Query.Interfaces;
using NcLink.Client.Query.Models;
using NcLink.Protocol;
using NcLink.Protocol.Contracts;

namespace NcLink.Client.Query.Engine;

/// <summary>
/// 数据项读取：官方手册 5.1「查询/设置相关参数」中的<b>查询</b>部分
/// （<c>get_value</c> / <c>get_length</c> / <c>get_keys</c> / <c>get_attributes</c>），
/// 以及设备模型探测与采样订阅的实现。
///
/// 写入在 <c>NcLink.Client.Set.Engine.DataItemWriter</c>——两者走同一个数据项端点，
/// 但操作名与响应结构不同，因此分开实现，避免"名为查询、实则也写"的误导。
/// 文件传输两者都用，见 <c>FileTransfer</c> 的构造函数。
/// </summary>
internal sealed class DataItemReader : IQuery, IDataItemReader
{
    private readonly RequestPipeline _pipeline;
    private readonly DeviceModelCache _models;
    private readonly SampleSubscriptions _sampling;
    private readonly INcLinkCodec _codec;
    private readonly INcLinkTopics _topics;

    public DataItemReader(
        RequestPipeline pipeline,
        DeviceModelCache models,
        SampleSubscriptions sampling,
        INcLinkCodec codec,
        INcLinkTopics topics)
    {
        _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
        _models = models ?? throw new ArgumentNullException(nameof(models));
        _sampling = sampling ?? throw new ArgumentNullException(nameof(sampling));
        _codec = codec ?? throw new ArgumentNullException(nameof(codec));
        _topics = topics ?? throw new ArgumentNullException(nameof(topics));
    }

    #region 设备模型

    /// <inheritdoc />
    public Task<DeviceModel> ProbeAsync(
        string deviceId, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        => _models.ProbeAsync(deviceId, timeout, cancellationToken);

    /// <inheritdoc />
    public Task<DeviceModel> GetModelAsync(
        string deviceId, bool refresh = false, CancellationToken cancellationToken = default)
        => _models.GetAsync(deviceId, refresh, null, cancellationToken);

    /// <inheritdoc />
    public void RegisterModel(DeviceModel model) => _models.Set(model);

    #endregion

    #region 查询

    public async Task<IReadOnlyList<DataItemResult>> QueryAsync(
        string deviceId,
        IEnumerable<DataItemRequest> items,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);

        var model = await _models.GetAsync(deviceId, false, timeout, cancellationToken).ConfigureAwait(false);
        var resolved = DataItemMessages.Resolve(model, items);

        var request = new QueryRequestMessage
        {
            Ids = resolved.Select(r => new QueryRequestItemMessage
            {
                Id = r.ItemId,
                Params = DataItemMessages.BuildQueryParams(r.Item)
            }).ToList()
        };

        var topic = _topics.QueryRequest(deviceId);
        var response = await _pipeline.SendAsync(
            topic,
            new RequestContext(topic, string.Empty, deviceId, resolved.Select(r => r.ItemId).ToArray()),
            request,
            matcher: static ctx => DataItemMessages.IsExpectedResponse(ctx, "\"values\""),
            timeout: timeout,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var message = _codec.Decode<QueryResponseMessage>(response.Payload)
                      ?? throw new NcLinkProtocolException(
                          $"查询响应解析失败：{response.Topic}", topic: response.Topic);

        var results = new List<DataItemResult>();
        foreach (var value in message.Values ?? [])
            results.Add(new DataItemResult(value.Id ?? string.Empty, value.IsOk, value.Reason, value.Values ?? []));

        // 设备回了 OK 却一个数据项都没有：按"设备不可用"处理，比返回空结果更可操作
        if (results.Count == 0)
            throw new NcLinkDeviceUnavailableException(deviceId);

        return results;
    }

    public async Task<DataItemResult> QueryItemAsync(
        string deviceId,
        DataItemRequest item,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var results = await QueryAsync(deviceId, [item], timeout, cancellationToken).ConfigureAwait(false);
        return results[0];
    }

    #endregion

    #region 采样

    /// <inheritdoc />
    public Task<ISampleSubscription> SubscribeSampleAsync(
        string deviceId, string channelId, CancellationToken cancellationToken = default)
        => _sampling.SubscribeAsync(deviceId, channelId, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<ISampleSubscription>> SubscribeAllSamplesAsync(
        string deviceId, CancellationToken cancellationToken = default)
        => _sampling.SubscribeAllAsync(deviceId, cancellationToken);

    #endregion
}

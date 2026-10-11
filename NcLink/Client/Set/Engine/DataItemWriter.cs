using NcLink.Client.Connection.Engine;
using NcLink.Client.Devices.Engine;
using NcLink.Client.Query.Models;
using NcLink.Client.Query.Engine;
using NcLink.Client.Set.Interfaces;
using NcLink.Protocol.Interfaces;
using NcLink.Protocol.Models;
using NcLink.Client.Files.Interfaces;

namespace NcLink.Client.Set.Engine;

/// <summary>
/// 数据项写入：官方手册 5.1「查询/设置相关参数」中的<b>设置</b>部分
/// （<c>set_value</c>，含 <c>offset</c> 位偏移）。
///
/// 读取在 <c>NcLink.Client.Query.Engine.DataItemReader</c>。
/// 文件的建目录/删除不在这里——那属于官方 5.2，见 <c>IFileTransfer</c>。
/// </summary>
internal sealed class DataItemWriter : ISet, IDataItemWriter
{
    private readonly RequestPipeline _pipeline;
    private readonly DeviceModelCache _models;
    private readonly INcLinkCodec _codec;
    private readonly INcLinkTopics _topics;

    public DataItemWriter(
        RequestPipeline pipeline,
        DeviceModelCache models,
        INcLinkCodec codec,
        INcLinkTopics topics)
    {
        _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
        _models = models ?? throw new ArgumentNullException(nameof(models));
        _codec = codec ?? throw new ArgumentNullException(nameof(codec));
        _topics = topics ?? throw new ArgumentNullException(nameof(topics));
    }

    public async Task<IReadOnlyList<SetItemResult>> SetAsync(
        string deviceId,
        IEnumerable<DataItemRequest> items,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);

        var model = await _models.GetAsync(deviceId, false, timeout, cancellationToken).ConfigureAwait(false);
        var resolved = DataItemMessages.Resolve(model, items);

        var request = new SetRequestMessage
        {
            Values = resolved.Select(r => new SetRequestItemMessage
            {
                Id = r.ItemId,
                Params = DataItemMessages.BuildSetParams(r.Item)
            }).ToList()
        };

        var message = await DataItemMessages.ExchangeAsync<SetResponseMessage>(
            _pipeline, _codec, _topics.SetRequest(deviceId), deviceId, resolved, request,
            DataItemMessages.SetStructureMarker, "设置", timeout, cancellationToken).ConfigureAwait(false);

        return (message.Results ?? [])
            .Select(r => new SetItemResult(r.Id ?? string.Empty, r.IsOk, r.Reason))
            .ToList();
    }

    public async Task<bool> SetItemAsync(
        string deviceId,
        DataItemRequest item,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var results = await SetAsync(deviceId, [item], timeout, cancellationToken).ConfigureAwait(false);
        return results.Count > 0 && results[0].Succeeded;
    }
}

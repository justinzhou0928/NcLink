using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NcLink.Protocol;
using NcLink.Logging;
using NcLink.Client.Connection;
using NcLink.Client.Devices;

namespace NcLink.Client.Devices;

/// <summary><see cref="IDeviceProbe"/> 的默认实现。</summary>
internal sealed partial class DeviceProbe : IDeviceProbe
{
    private readonly IRequestPipeline _pipeline;
    private readonly INcLinkCodec _codec;
    private readonly INcLinkTopics _topics;
    private readonly ILogger<DeviceProbe> _logger;

    public DeviceProbe(
        IRequestPipeline pipeline,
        INcLinkCodec codec,
        INcLinkTopics topics,
        ILoggerFactory loggerFactory)
    {
        _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
        _codec = codec ?? throw new ArgumentNullException(nameof(codec));
        _topics = topics ?? throw new ArgumentNullException(nameof(topics));
        _logger = (loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory)))
            .CreateLogger<DeviceProbe>();
    }

    public async Task<string> ProbeModelJsonAsync(
        string deviceId, TimeSpan? timeout, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);

        var topic = _topics.ProbeQueryRequest(deviceId);
        var context = new RequestContext(topic, deviceId, []);

        var response = await _pipeline.SendAsync(
            topic,
            context,
            new ProbeQueryRequestMessage(),
            matcher: static ctx => ContainsModelPayload(ctx.ResponsePayload),
            timeout: timeout,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var message = _codec.Decode<ProbeQueryResponseMessage>(response.Payload)
                      ?? throw new NcLinkProtocolException(
                          $"探测响应解析失败：{response.Topic}", topic: response.Topic);

        if (!string.IsNullOrEmpty(message.Code) && NcLinkCodeNames.Parse(message.Code) != NcLinkCode.Ok)
            throw new NcLinkDeviceRejectedException(deviceId, itemId: null, reason: $"探测被拒绝（code={message.Code}）");

        if (message.Probe.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
            throw new NcLinkProtocolException(
                $"设备 {deviceId} 的探测响应中没有 probe 字段。",
                topic: response.Topic)
            { DeviceId = deviceId };

        // 把 probe 对象原样序列化成 model.json 文本，交给 DeviceModelParser 解析
        var json = message.Probe.GetRawText();
        ProbeLog.LogProbed(_logger, deviceId, json.Length);
        return json;
    }

    /// <summary>
    /// 报文体是否为探测响应：含非空的 <c>probe</c> 字段。
    ///
    /// 实测要点：官方探测响应<b>不回显 <c>@id</c></b>，
    /// 因此这里只能靠"结构像不像"来认领。
    /// 先做关键字预筛，避免对每条无关响应都跑一次反序列化。
    /// </summary>
    private static bool ContainsModelPayload(ReadOnlyMemory<byte> payload)
    {
        // 先按字节找标记（零分配），命中了才反序列化；反序列化也直接吃 UTF-8 字节
        if (payload.Span.IndexOf("\"probe\""u8) < 0) return false;

        try
        {
            var probe = NcLinkJson.Deserialize<ProbeQueryResponseMessage>(payload.Span)?.Probe;
            return probe is { ValueKind: JsonValueKind.Object or JsonValueKind.Array };
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }

}

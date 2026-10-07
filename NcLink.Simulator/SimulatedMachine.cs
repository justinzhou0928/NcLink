using System.Text;
using System.Text.Json;
using MQTTnet;
using MQTTnet.Protocol;
using NcLink.Protocol.Contracts;
using NcLink.Protocol;
using NcLink.Client.Devices.Models;
using NcLink.Client.Devices.Parsers;

namespace NcLink.Simulator;

/// <summary>
/// 一台模拟数控机床。
///
/// 它是<b>设备侧协议的参考实现</b>，也是离线联调的对端：
/// <list type="bullet">
/// <item>订阅 <c>Query/Request/{SN}</c>、<c>Set/Request/{SN}</c>、<c>Probe/Query/Request/{SN}</c>；</item>
/// <item>响应发布到对应 Response 主题，并回显请求的 <c>@id</c> 以便客户端精确关联；</item>
/// <item>采样数据发布到 <c>Sample/{channelId}</c>。</item>
/// </list>
/// </summary>
public sealed class SimulatedMachine : IAsyncDisposable
{
    // 刻意不复用客户端的 NcLinkProtocolPaths.File：设备侧应独立拥有自己的路径认知，
    // 否则客户端路径写错时测试会跟着错。不含前导斜杠是设备侧视角。见 AGENTS.md §3。
    private const string FileNodePath = "MACHINE/CONTROLLER/FILE";

    private readonly SimulatedMachineOptions _options;
    private readonly IMqttClient _mqtt;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DeviceModel _model;
    private readonly Dictionary<string, string> _tagByItemId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<object?>> _store = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _files = new(StringComparer.Ordinal);
    private readonly Encoding _gcodeEncoding;
    private readonly INcLinkTopics _topics;

    private Task? _sampleLoop;
    private int _sampleSequence;
    private bool _stopped;

    public SimulatedMachine(SimulatedMachineOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));

        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        _gcodeEncoding = Encoding.GetEncoding("GB2312");

        _topics = options.Topics;
        _model = DeviceModelParser.Parse(options.ModelJson, options.DeviceId);
        BuildTagIndex();
        SeedData();

        _mqtt = new MqttClientFactory().CreateMqttClient();
    }

    public string DeviceId => _options.DeviceId;

    public long QueryCount { get; private set; }

    public long SetCount { get; private set; }

    public long ProbeCount { get; private set; }

    public long PublishedSampleCount { get; private set; }

    #region 生命周期

    /// <summary>
    /// 观测后台采样循环的失败。
    ///
    /// <c>_sampleLoop = RunSampleLoopAsync(...)</c> 是<b>故意不 await</b> 的（循环会一直运行），
    /// 因此它抛出的异常若不单独观测就会被静默吞掉——模拟器会"看起来在跑但不再发采样"。
    /// 这里只过滤掉停机导致的取消异常。
    /// </summary>
    private void ObserveSampleLoopFailure(Task loop)
        => _ = loop.ContinueWith(
            static (task, state) =>
            {
                // 停机取消属预期，不算失败
                if (task.Exception?.InnerException is OperationCanceledException) return;
                if (task.Exception is null) return;

                var machine = (SimulatedMachine)state!;
                Console.Error.WriteLine(
                    $"[模拟器 {machine.DeviceId}] 采样循环异常退出：{task.Exception.GetBaseException().Message}");
            },
            this,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        var builder = new MqttClientOptionsBuilder()
            .WithTcpServer(_options.Host, _options.Port)
            .WithClientId(DeviceId)
            .WithCleanSession(true)
            .WithTimeout(TimeSpan.FromSeconds(10));

        if (!string.IsNullOrEmpty(_options.UserName))
            builder = builder.WithCredentials(_options.UserName, _options.Password);

        _mqtt.ApplicationMessageReceivedAsync += OnMessageAsync;
        await _mqtt.ConnectAsync(builder.Build(), cancellationToken).ConfigureAwait(false);

        var topics = new MqttClientSubscribeOptionsBuilder()
            .WithTopicFilter(f => f.WithTopic(_topics.QueryRequest(DeviceId))
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtMostOnce))
            .WithTopicFilter(f => f.WithTopic(_topics.SetRequest(DeviceId))
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtMostOnce))
            .WithTopicFilter(f => f.WithTopic(_topics.ProbeQueryRequest(DeviceId))
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtMostOnce))
            .Build();

        await _mqtt.SubscribeAsync(topics, cancellationToken).ConfigureAwait(false);

        // 设备上线后向平台注册
        await PublishAsync(_options.Topics.RegisterRequest,
            new RegisterRequestMessage { DeviceId = DeviceId }, cancellationToken).ConfigureAwait(false);

        // 采样循环本身就是异步的，直接持有 Task 即可（无需 Task.Run 再占一个线程池线程）
        _sampleLoop = RunSampleLoopAsync(_lifetime.Token);
        ObserveSampleLoopFailure(_sampleLoop);
    }

    /// <summary>
    /// 模拟设备掉线：断开 MQTT 并停止采样，但对象仍可继续释放。
    /// 用于测试「一台设备掉线后其余设备是否仍可访问」，以及「迟到响应是否被误领」。
    /// </summary>
    public async Task StopAsync()
    {
        if (_stopped) return;
        _stopped = true;

        await _lifetime.CancelAsync().ConfigureAwait(false);

        if (_sampleLoop is not null)
        {
            try { await _sampleLoop.ConfigureAwait(false); }
            catch (Exception) { /* 取消导致的退出 */ }
            _sampleLoop = null;
        }

        try
        {
            if (_mqtt.IsConnected) await _mqtt.DisconnectAsync().ConfigureAwait(false);
        }
        catch (Exception) { /* 掉线模拟中忽略 */ }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);

        _mqtt.ApplicationMessageReceivedAsync -= OnMessageAsync;
        _mqtt.Dispose();
        _lifetime.Dispose();
    }

    #endregion

    #region 索引与数据

    /// <summary>建立「数据项 id → 存储键」映射。存储键用标识（如 VARIABLE@REG_X）。</summary>
    private void BuildTagIndex()
    {
        foreach (var node in _model.Nodes)
        {
            if (node.IsSampleChannel) continue;
            if (string.IsNullOrEmpty(node.Id)) continue;

            // 轴部件节点（POSITION/SPEED/CURRENT）在模型里是叶子、且可能与数据项 id 重复，
            // 这里只索引唯一命中的情况，重复的交给显式映射处理。
            _tagByItemId.TryAdd(node.Id, node.Tag.Segment);
        }
    }

    private void SeedData()
    {
        // 寄存器
        foreach (var tag in new[] { "REG_X", "REG_Y", "REG_F", "REG_G", "REG_R", "REG_B" })
        {
            var values = new List<object?>();
            for (var i = 0; i < 256; i++) values.Add(tag == "REG_X" ? i * 3 : i);
            _store["VARIABLE@" + tag] = values;
        }

        // 轴数据（按索引语义填关键位）
        foreach (var axis in new[] { "AXIS_0", "AXIS_1", "AXIS_2", "AXIS_5" })
        {
            var values = Enumerable.Repeat<object?>(0, 57).ToList();
            values[0] = axis;        // 轴名
            values[1] = 1;           // 轴类型
            values[6] = 123456;      // 机床实际位置
            values[17] = 1000;       // 指令速度
            values[18] = 990;        // 实际速度
            values[28] = 2.5;        // 驱动单元电流
            values[29] = 3.1;        // 负载电流
            values[30] = 12.0;       // 额定电流
            values[31] = 1;          // 回零完成
            _store["VARIABLE@" + axis] = values;
        }

        // 系统数据
        var system = Enumerable.Repeat<object?>(0, 40).ToList();
        system[0] = 1;
        system[11] = "NCK-1.24";
        system[14] = "HNC8-1.24";
        system[25] = DeviceId;
        system[26] = "HNC-8";
        _store["VARIABLE@SYS"] = system;

        // 通道数据
        var channel = Enumerable.Repeat<object?>(0, 60).ToList();
        channel[0] = 1;
        channel[4] = "CH0";
        channel[6] = 3000.0;
        channel[7] = 2950.0;
        channel[16] = 1;
        channel[27] = 1;
        channel[36] = 42;
        _store["VARIABLE@CHAN_0"] = channel;

        _store["VARIABLE@MACRO"] = Enumerable.Range(0, 100).Select(i => (object?)(i * 10)).ToList();

        // 简单数据项
        _store["STATUS"] = [1];
        _store["FEED_SPEED"] = [3000];
        _store["FEED_OVERRIDE"] = [100];
        _store["SPINDLE_OVERRIDE"] = [100];
        _store["PART_COUNT"] = [42];
        _store["PROGRAM"] = ["O0001"];
        _store["LINE_NUMBER"] = [17];
        _store["TOOL_NUMBER"] = [2];
        _store["PROGRAM_NUMBER"] = [1];
        _store["WARNING"] = [];          // 无报警
        _store["PROGID_MAP"] = [];
        _store["EVENT"] = [0];
        _store["SAMPLE_PERIOD"] = [1];

        // 刀具参数
        _store["TOOL_PARAM"] = Enumerable.Range(0, 20)
            .Select(_ => (object?)new Dictionary<string, object?>
            {
                ["length"] = 100, ["length_abrasion"] = 0, ["radius"] = 5, ["radius_abrasion"] = 0
            })
            .ToList();

        // 坐标系
        _store["COORDINATE"] = Enumerable.Range(0, 16)
            .Select(_ => (object?)new Dictionary<string, object?> { ["x"] = 0, ["y"] = 0, ["z"] = 0, ["c"] = 0 })
            .ToList();

        // 参数
        _store["PARAMETER"] = Enumerable.Range(0, 200).Select(i => (object?)(i == 50 ? 2 : 1000 + i)).ToList();

        // G 代码文件
        _files["O0001"] = "%1234\nG54M03S2000F3000\nG01 X100\nY100\nZ100\nM30\n%\n";
        _files["O0002"] = "%5678\nG00 X0 Y0\nM99\n%\n";
        _store["FILE"] = [];
    }

    #endregion

    #region 报文处理

    private async Task OnMessageAsync(MqttApplicationMessageReceivedEventArgs args)
    {
        var topic = args.ApplicationMessage.Topic;
        var text = Encoding.UTF8.GetString(BuffersToArray(args.ApplicationMessage.Payload));

        try
        {
            if (topic.StartsWith(NcLinkTopicPrefixes.ProbeQueryRequest, StringComparison.Ordinal))
            {
                ProbeCount++;
                await HandleProbeAsync(text).ConfigureAwait(false);
                return;
            }

            if (topic.StartsWith(NcLinkTopicPrefixes.QueryRequest, StringComparison.Ordinal))
            {
                QueryCount++;
                await HandleQueryAsync(text).ConfigureAwait(false);
                return;
            }

            if (topic.StartsWith(NcLinkTopicPrefixes.SetRequest, StringComparison.Ordinal))
            {
                SetCount++;
                await HandleSetAsync(text).ConfigureAwait(false);
            }
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[{DeviceId}] 处理 {topic} 出错：{e.GetType().Name}: {e.Message}");
        }
    }

    private async Task HandleProbeAsync(string text)
    {
        var request = NcLinkJson.Deserialize<ProbeQueryRequestMessage>(text);

        // 官方实测：探测响应的模型嵌在 probe 字段里（对象），Code 为小写 ok，
        // 且不回显 @id —— 与官方模拟器保持一致，见
        // docs/official-simulator-verified-protocol.md
        var response = new ProbeQueryResponseMessage
        {
            CorrelationId = request?.CorrelationId,
            Code = "ok",
            Probe = System.Text.Json.JsonDocument.Parse(_options.ModelJson).RootElement.Clone()
        };

        await PublishAsync(_topics.ProbeQueryResponse(DeviceId), response, CancellationToken.None)
            .ConfigureAwait(false);
    }

    private async Task HandleQueryAsync(string text)
    {
        var request = NcLinkJson.Deserialize<QueryRequestMessage>(text);

        var response = new QueryResponseMessage
        {
            CorrelationId = _options.EchoCorrelationId ? request?.CorrelationId : null,
            Values = []
        };

        foreach (var item in request?.Ids ?? [])
        {
            response.Values!.Add(BuildQueryResponseItem(item));
        }

        await PublishAsync(_topics.QueryResponse(DeviceId), response, CancellationToken.None)
            .ConfigureAwait(false);
    }

    private QueryResponseItemMessage BuildQueryResponseItem(QueryRequestItemMessage request)
    {
        var key = ResolveStoreKey(request.Id);
        var operation = request.Params?.Operation ?? NcLinkOperationNames.GetValue;

        var result = new QueryResponseItemMessage
        {
            Id = request.Id,
            Code = NcLinkCodeNames.Ok,
            Params = new QueryParamsMessage
            {
                Indexes = request.Params?.Indexes,
                Keys = request.Params?.Keys,
                Offset = request.Params?.Offset,
                Length = request.Params?.Length,
                Operation = operation
            }
        };

        try
        {
            switch (operation)
            {
                case NcLinkOperationNames.GetKeys:
                    result.Values = _files.Keys
                        .OrderBy(k => k, StringComparer.Ordinal)
                        .Select(k => JsonSerializer.SerializeToElement(k))
                        .ToList();
                    break;

                case NcLinkOperationNames.GetAttributes:
                    result.Values = (request.Params?.Keys ?? [])
                        .Select(name => JsonSerializer.SerializeToElement(new Dictionary<string, object?>
                        {
                            ["changeTime"] = "2024/01/01 00:00:00",
                            ["size"] = _files.TryGetValue(name, out var content) ? _gcodeEncoding.GetByteCount(content) : 0,
                            ["type"] = "file"
                        }))
                        .ToList();
                    break;

                case NcLinkOperationNames.GetValue when request.Params?.Keys is { Count: > 0 } keys:
                    result.Values = [JsonSerializer.SerializeToElement(ReadFileChunk(keys[0], request.Params))];
                    break;

                default:
                    result.Values = ReadValues(key, request.Params);
                    break;
            }
        }
        catch (Exception e)
        {
            result.Code = NcLinkCodeNames.Ng;
            result.Reason = e.Message;
            result.Values = null;
        }

        return result;
    }

    /// <summary>读取文件分块，返回十六进制字符串（协议要求）。</summary>
    private string ReadFileChunk(string fileName, QueryParamsMessage parameters)
    {
        if (!_files.TryGetValue(fileName, out var content))
            throw new InvalidOperationException($"文件不存在：{fileName}");

        var bytes = _gcodeEncoding.GetBytes(content);
        var offset = (int)(parameters.Offset ?? 0);
        var length = (int)(parameters.Length ?? bytes.Length);

        if (offset >= bytes.Length) return string.Empty;

        var count = Math.Min(length, bytes.Length - offset);
        return Convert.ToHexStringLower(bytes.AsSpan(offset, count));
    }

    private List<JsonElement> ReadValues(string key, QueryParamsMessage? parameters)
    {
        if (!_store.TryGetValue(key, out var values))
            throw new InvalidOperationException($"模拟器没有该数据项：{key}");

        if (string.Equals(parameters?.Operation, NcLinkOperationNames.GetLength, StringComparison.Ordinal))
            return [JsonSerializer.SerializeToElement(values.Count)];

        var indexes = parameters?.Indexes;
        if (indexes is null or { Count: 0 })
            return values.Select(ToJson).ToList();

        var selected = new List<JsonElement>();
        foreach (var rangeText in indexes)
        {
            if (!IndexRange.TryParse(rangeText, out var range)) continue;

            foreach (var index in range.Enumerate())
                if (index >= 0 && index < values.Count)
                    selected.Add(ToJson(values[index]));
        }

        return selected;
    }

    private async Task HandleSetAsync(string text)
    {
        var request = NcLinkJson.Deserialize<SetRequestMessage>(text);

        var response = new SetResponseMessage
        {
            CorrelationId = _options.EchoCorrelationId ? request?.CorrelationId : null,
            Results = []
        };

        foreach (var item in request?.Values ?? [])
        {
            var key = ResolveStoreKey(item.Id);
            var operation = item.Params?.Operation ?? NcLinkOperationNames.SetValue;

            var result = new SetResponseItemMessage
            {
                Id = item.Id,
                Code = NcLinkCodeNames.Ok,
                Params = new SetParamsMessage { Index = item.Params?.Index, Offset = item.Params?.Offset }
            };

            try
            {
                switch (operation)
                {
                    case NcLinkOperationNames.Add:
                    case NcLinkOperationNames.Delete:
                        ApplyFileOperation(operation, item.Params?.Key);
                        break;

                    case NcLinkOperationNames.SetValue when item.Params?.Key is { } fileKey:
                        WriteFile(fileKey, item.Params);
                        break;

                    default:
                        ApplySetValue(key, item.Params);
                        break;
                }
            }
            catch (Exception e)
            {
                result.Code = NcLinkCodeNames.Ng;
                result.Reason = e.Message;
            }

            response.Results!.Add(result);
        }

        await PublishAsync(_topics.SetResponse(DeviceId), response, CancellationToken.None)
            .ConfigureAwait(false);
    }

    private void ApplyFileOperation(string operation, string? key)
    {
        if (string.IsNullOrEmpty(key)) return;

        if (operation == NcLinkOperationNames.Delete)
            _files.Remove(key);
        // Add（新建文件夹）在模拟器里不产生实际状态
    }

    /// <summary>下发文件分块：value 是十六进制字符串，按 GB2312 解码。</summary>
    private void WriteFile(string key, SetParamsMessage parameters)
    {
        if (parameters.Value is not { } value) return;

        var hex = value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;
        var bytes = Convert.FromHexString(hex);
        var text = _gcodeEncoding.GetString(bytes);

        var offset = parameters.Offset ?? 0;
        if (offset > 0 && _files.TryGetValue(key, out var existing))
            _files[key] = existing + text;
        else
            _files[key] = text;
    }

    private void ApplySetValue(string key, SetParamsMessage? parameters)
    {
        if (!_store.TryGetValue(key, out var values))
            throw new InvalidOperationException($"模拟器没有该数据项：{key}");

        if (parameters?.Index is not { } index) return;
        if (index < 0 || index >= values.Count) return;
        if (parameters.Value is not { } value) return;

        // offset 存在表示设置某一位
        if (parameters.Offset is { } bit)
        {
            var current = Convert.ToInt32(values[(int)index]);
            var bitValue = value.ValueKind == JsonValueKind.Number ? value.GetInt32() : 0;
            values[(int)index] = bitValue == 1 ? current | (1 << bit) : current & ~(1 << bit);
            return;
        }

        values[(int)index] = value.ValueKind switch
        {
            JsonValueKind.Number => value.TryGetInt32(out var i) ? i : value.GetDouble(),
            JsonValueKind.String => value.GetString(),
            _ => value.GetRawText()
        };
    }

    /// <summary>把模型 id 解析为内部存储键（标识，如 VARIABLE@REG_X）。</summary>
    private string ResolveStoreKey(string? itemId)
    {
        if (string.IsNullOrEmpty(itemId)) return string.Empty;
        if (_store.ContainsKey(itemId)) return itemId;

        if (_tagByItemId.TryGetValue(itemId, out var tag) && _store.ContainsKey(tag))
            return tag;

        // 轴部件节点（POSITION/SPEED/CURRENT）语义上取轴数据的首值
        if (itemId.StartsWith("010350", StringComparison.Ordinal)) return "VARIABLE@AXIS_0";
        if (itemId.StartsWith("010351", StringComparison.Ordinal)) return "VARIABLE@AXIS_1";
        if (itemId.StartsWith("010352", StringComparison.Ordinal)) return "VARIABLE@AXIS_2";

        return itemId;
    }

    #endregion

    #region 采样

    private async Task RunSampleLoopAsync(CancellationToken cancellationToken)
    {
        if (_options.SampleChannels.Count == 0) return;

        var tasks = _options.SampleChannels
            .Select(channel => RunChannelAsync(channel, cancellationToken))
            .ToArray();

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task RunChannelAsync(SimulatedSampleChannel channel, CancellationToken cancellationToken)
    {
        var interval = Math.Max(1, channel.SampleIntervalMs);
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(interval));

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false)) break;
            }
            catch (OperationCanceledException) { break; }

            var items = new List<SampleItemMessage>();
            foreach (var itemId in channel.ItemIds)
            {
                var key = ResolveStoreKey(itemId);
                if (!_store.TryGetValue(key, out var values)) continue;

                // 空数组也是有效采样值（例如无报警时的 WARNING）
                items.Add(new SampleItemMessage
                {
                    Id = itemId,
                    Data = values.Count > 0 ? [ToJson(values[0])] : []
                });
            }

            var frame = new SampleMessage
            {
                CorrelationId = Guid.NewGuid().ToString(),
                Id = channel.ChannelId,
                BeginTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Data = items
            };

            try
            {
                // 主题为 Sample/{设备 SN}/{通道 id}——官方实测确认含 SN
                await PublishAsync(_topics.Sample(DeviceId, channel.ChannelId), frame, cancellationToken)
                    .ConfigureAwait(false);
                Interlocked.Increment(ref _sampleSequence);
                PublishedSampleCount++;
            }
            catch (OperationCanceledException) { break; }
            catch (Exception) { /* 发送失败不终止采样循环 */ }
        }
    }

    #endregion

    #region 发布

    private async Task PublishAsync<T>(string topic, T message, CancellationToken cancellationToken)
        where T : NcLinkEnvelope
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(message, message.GetType(), NcLinkJson.Options);

        var mqttMessage = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtMostOnce)
            .Build();

        await _mqtt.PublishAsync(mqttMessage, cancellationToken).ConfigureAwait(false);
    }

    private static JsonElement ToJson(object? value)
        => value is null ? JsonSerializer.SerializeToElement<object?>(null) : JsonSerializer.SerializeToElement(value, value.GetType());

    private static byte[] BuffersToArray(System.Buffers.ReadOnlySequence<byte> payload)
        => payload.Length > 0 ? System.Buffers.BuffersExtensions.ToArray(payload) : [];

    /// <summary>供诊断：本机是否仍连接。</summary>
    public bool IsConnected => _mqtt.IsConnected;
}

/// <summary>模拟机床的采样通道定义。</summary>
public sealed class SimulatedSampleChannel
{
    public required string ChannelId { get; init; }

    public required IReadOnlyList<string> ItemIds { get; init; }

    public int SampleIntervalMs { get; init; } = 1000;
}

/// <summary>模拟机床参数。</summary>
public sealed class SimulatedMachineOptions
{
    public required string DeviceId { get; init; }

    public string Host { get; init; } = "127.0.0.1";

    public int Port { get; init; } = 1883;

    public string? UserName { get; init; }

    public string? Password { get; init; }

    public required string ModelJson { get; init; }

    /// <summary>
    /// 是否在响应里回显请求的 <c>@id</c>。
    /// 真机行为需现场确认；设为 false 可验证客户端的降级匹配路径。
    /// </summary>
    public bool EchoCorrelationId { get; init; } = true;

    public IReadOnlyList<SimulatedSampleChannel> SampleChannels { get; init; } = [];

    /// <summary>主题规则（便于测试注入替身）。</summary>
    public INcLinkTopics Topics { get; init; } = NcLinkTopics.Default;

    #endregion

}

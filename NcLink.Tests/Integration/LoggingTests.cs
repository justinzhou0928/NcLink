using Microsoft.Extensions.Logging;
using System.Text;
using NcLink.Transport.Mqtt;

namespace NcLink.Tests.Integration;

/// <summary>
/// 日志验证：确认关键路径真的产出日志（不只是"编译通过"）。
///
/// 用一个内存捕获器订阅 <see cref="ILoggerFactory"/>，逐条记录日志事件，
/// 再断言连接、订阅、探测这些路径都打了点。
/// </summary>
public sealed class LoggingTests : IAsyncLifetime
{
    private readonly BrokerFixture _fixture = new("LOG", deviceCount: 1);
    private bool _skip;

    public async Task InitializeAsync() => _skip = !await _fixture.StartAsync();

    public async Task DisposeAsync() => await _fixture.DisposeAsync();

    [Fact]
    public async Task Client_ShouldEmitStructuredLogs_OnKeyPaths()
    {
        if (_skip) return;

        var capture = new LogCapture();
        await using var client = _fixture.CreateClient(loggerFactory: capture);

        await client.InitializeAsync();
        await client.ProbeAsync(_fixture.PrimaryDeviceId!);

        // 连接 / 订阅 / 探测都应留下记录
        Assert.Contains(capture.Records, r => r.EventId.Id == 2001);   // 正在连接
        Assert.Contains(capture.Records, r => r.EventId.Id == 2002);   // 已连接
        Assert.Contains(capture.Records, r => r.EventId.Id == 1001);   // 发送请求
        Assert.Contains(capture.Records, r => r.EventId.Id == 1002);   // 响应已关联

        // 日志文本里应带上设备 SN（结构化参数被正确填充）
        var probeLog = capture.Records.First(r => r.EventId.Id == 1001);
        Assert.Contains("LOG_DEV_00001", probeLog.Message);
    }

    [Fact]
    public async Task UnmatchedResponse_ShouldBeLogged()
    {
        if (_skip) return;

        var capture = new LogCapture();
        await using var client = _fixture.CreateClient(loggerFactory: capture);

        await client.InitializeAsync();
        await client.ProbeAsync(_fixture.PrimaryDeviceId!);

        var before = capture.Records.Count;

        // 用一条独立连接往响应主题投"野响应"：没人等待这条报文
        await using var intruder = new MqttTransport(new MqttTransportOptions
        {
            BrokerAddress = $"tcp://{BrokerFixture.DefaultHost}:{BrokerFixture.DefaultPort}",
            ClientId = $"intruder-{Guid.NewGuid():N}"[..20]
        });
        await intruder.ConnectAsync();
        await intruder.PublishAsync(
            $"Query/Response/{_fixture.PrimaryDeviceId}",
            Encoding.UTF8.GetBytes("""{"@id":"nobody-waits-for-this","values":[]}"""));

        await WaitUntilAsync(() => capture.Records.Skip(before).Any(r => r.EventId.Id == 1005));

        // 该响应不属于任何在途请求，应被记为"无人认领"而不是被误领
        Assert.Contains(capture.Records.Skip(before), r => r.EventId.Id == 1005);
    }

    /// <summary>轮询等待条件成立（带超时），避免固定 sleep。</summary>
    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition()) return;
            await Task.Delay(50);
        }
    }

    /// <summary>内存日志捕获器。</summary>
    private sealed class LogCapture : ILoggerFactory, ILoggerProvider
    {
        private readonly List<LogRecord> _records = [];
        private readonly object _gate = new();

        public IReadOnlyList<LogRecord> Records
        {
            get { lock (_gate) return _records.ToList(); }
        }

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(this, categoryName);

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public void Dispose()
        {
        }

        private void Add(LogRecord record)
        {
            lock (_gate) _records.Add(record);
        }

        private sealed class CapturingLogger : ILogger
        {
            private readonly LogCapture _owner;
            private readonly string _category;

            public CapturingLogger(LogCapture owner, string category)
            {
                _owner = owner;
                _category = category;
            }

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
                => _owner.Add(new LogRecord(_category, logLevel, eventId, formatter(state, exception)));
        }
    }

    private sealed record LogRecord(string Category, LogLevel Level, EventId EventId, string Message);
}

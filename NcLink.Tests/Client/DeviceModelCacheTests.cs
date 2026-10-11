using NcLink.Client.Connection.Exceptions;
using NcLink.Client.Devices.Engine;
using NcLink.Client.Devices.Interfaces;
using NcLink.Client.Devices.Models;
using NcLink.Client.Devices.Parsers;
using Microsoft.Extensions.Logging.Abstractions;

namespace NcLink.Tests.Client;

/// <summary>
/// <c>DeviceModelCache</c> 单元测试：<b>不需要 MQTT Broker</b>。
///
/// 重点是并发去重：多个并发请求同一台未缓存设备时，
/// 只应发生<b>一次</b>探测（一次 MQTT 往返），其余共享同一结果。
/// </summary>
public sealed class DeviceModelCacheTests
{
    private const string Device = "TEST_DEV";

    [Fact]
    public async Task ConcurrentGet_ShouldProbeOnlyOnce()
    {
        var probe = new CountingProbe();
        var cache = new DeviceModelCache(probe, NullLogger<DeviceModelCache>.Instance);

        // 8 个并发请求同一台未缓存设备
        var results = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(_ => cache.GetAsync(Device)));

        Assert.Equal(1, probe.CallCount);
        Assert.All(results, m => Assert.Same(results[0], m));   // 共享同一实例
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public async Task SecondGet_ShouldHitCacheWithoutProbing()
    {
        var probe = new CountingProbe();
        var cache = new DeviceModelCache(probe, NullLogger<DeviceModelCache>.Instance);

        await cache.GetAsync(Device);
        await cache.GetAsync(Device);
        await cache.GetAsync(Device);

        Assert.Equal(1, probe.CallCount);
    }

    [Fact]
    public async Task Refresh_ShouldProbeAgainEvenIfCached()
    {
        var probe = new CountingProbe();
        var cache = new DeviceModelCache(probe, NullLogger<DeviceModelCache>.Instance);

        await cache.GetAsync(Device);
        await cache.GetAsync(Device, refresh: true);

        Assert.Equal(2, probe.CallCount);
    }

    [Fact]
    public async Task FailedProbe_ShouldNotBeCachedSoNextCallRetries()
    {
        var probe = new CountingProbe { FailuresRemaining = 1 };
        var cache = new DeviceModelCache(probe, NullLogger<DeviceModelCache>.Instance);

        // 第一次失败
        await Assert.ThrowsAsync<NcLinkDeviceUnavailableException>(() => cache.GetAsync(Device));
        Assert.Equal(1, probe.CallCount);

        // 失败不能被永久缓存，否则一次瞬时故障会变成"该设备探测总是失败"
        var model = await cache.GetAsync(Device);
        Assert.Equal(2, probe.CallCount);
        Assert.Equal(Device, model.DeviceId);
    }

    [Fact]
    public async Task ConcurrentGet_WhenProbeFails_ShouldSurfaceFailureToAllCallers()
    {
        var probe = new CountingProbe { FailuresRemaining = 1, DelayMs = 30 };
        var cache = new DeviceModelCache(probe, NullLogger<DeviceModelCache>.Instance);

        var tasks = Enumerable.Range(0, 6).Select(_ => cache.GetAsync(Device)).ToArray();

        foreach (var task in tasks)
            await Assert.ThrowsAsync<NcLinkDeviceUnavailableException>(() => task);

        // 6 个并发调用共享同一次失败探测
        Assert.Equal(1, probe.CallCount);
    }

    [Fact]
    public async Task RequireCached_BeforeProbe_ShouldThrowInvalidRequest()
    {
        var cache = new DeviceModelCache(new CountingProbe(), NullLogger<DeviceModelCache>.Instance);

        await Assert.ThrowsAsync<NcLinkInvalidRequestException>(() => Task.FromResult(cache.RequireCached(Device)));
    }

    [Fact]
    public void Set_ShouldPopulateCacheForReadOnlyQueries()
    {
        var cache = new DeviceModelCache(new CountingProbe(), NullLogger<DeviceModelCache>.Instance);
        var model = SampleDeviceModel.Parse(Device);

        cache.Set(model);

        Assert.Equal(1, cache.Count);
        Assert.Same(model, cache.RequireCached(Device));
        Assert.Contains(Device, cache.CachedDeviceIds);
    }

    [Fact]
    public void GetChannelItemIds_ShouldReturnConfiguredIdsInOrder()
    {
        var cache = new DeviceModelCache(new CountingProbe(), NullLogger<DeviceModelCache>.Instance);
        var model = SampleDeviceModel.Parse("DEV_A");
        cache.Set(model);

        var channel = model.SampleChannels.Values.First();

        // 采样报文不带数据项 id，映射依据就是这里返回的顺序清单
        Assert.Equal(channel.ItemIds, cache.GetChannelItemIds("DEV_A", channel.ChannelId));
    }

    [Fact]
    public void GetChannelItemIds_UnknownDeviceOrChannel_ShouldReturnEmpty()
    {
        var cache = new DeviceModelCache(new CountingProbe(), NullLogger<DeviceModelCache>.Instance);
        var model = SampleDeviceModel.Parse("DEV_A");
        cache.Set(model);

        var channel = model.SampleChannels.Keys.First();

        Assert.Empty(cache.GetChannelItemIds("NO_SUCH_DEVICE", channel));
        Assert.Empty(cache.GetChannelItemIds("DEV_A", "no_such_channel"));
    }

    [Fact]
    public void GetChannelItemIds_TwoDevicesSharingChannelId_ShouldBeScopedByDevice()
    {
        // 两台设备共用同一通道 id 时互不干扰——这正是主题带 SN 带来的确定性
        var cache = new DeviceModelCache(new CountingProbe(), NullLogger<DeviceModelCache>.Instance);
        cache.Set(SampleDeviceModel.Parse("DEV_A"));
        cache.Set(SampleDeviceModel.Parse("DEV_B"));

        var channel = SampleDeviceModel.Parse("X").SampleChannels.Keys.First();

        Assert.NotEmpty(cache.GetChannelItemIds("DEV_A", channel));
        Assert.NotEmpty(cache.GetChannelItemIds("DEV_B", channel));
    }

    [Fact]
    public async Task InvalidDeviceId_ShouldThrow()
    {
        var cache = new DeviceModelCache(new CountingProbe(), NullLogger<DeviceModelCache>.Instance);

        await Assert.ThrowsAnyAsync<ArgumentException>(() => cache.GetAsync("  "));
    }

    [Fact]
    public async Task ConcurrentGet_WhenFirstCallerCancels_ShouldNotCancelOthers()
    {
        // 回归：共享探测任务曾经闭包捕获"第一个调用方"的取消令牌，
        // 于是 A 取消会把 B 一起打成取消——尽管 B 自己的令牌从未取消。
        // 现在共享任务用 CancellationToken.None，取消只作用于各自的等待。
        var probe = new CountingProbe { DelayMs = 120 };
        var cache = new DeviceModelCache(probe, NullLogger<DeviceModelCache>.Instance);

        using var first = new CancellationTokenSource();
        var firstCall = cache.GetAsync(Device, cancellationToken: first.Token);
        var secondCall = cache.GetAsync(Device);

        first.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => firstCall);

        // 第二个调用方不受影响，且两者共用同一次探测
        var model = await secondCall.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(Device, model.DeviceId);
        Assert.Equal(1, probe.CallCount);
    }

    /// <summary>可计数的假探测：可选地延迟、可选地失败若干次。</summary>
    private sealed class CountingProbe : IDeviceProbe
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        /// <summary>模拟一次 MQTT 往返的耗时；并发测试需要它拉长窗口。</summary>
        public int DelayMs { get; init; } = 20;

        /// <summary>前 N 次调用失败，用于验证"失败不被永久缓存"。</summary>
        public int FailuresRemaining { get; init; }

        public async Task<string> ProbeModelJsonAsync(
            string deviceId, TimeSpan? timeout, CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref _callCount);

            if (DelayMs > 0)
                await Task.Delay(DelayMs, cancellationToken);

            if (call <= FailuresRemaining)
                throw new NcLinkDeviceUnavailableException(deviceId);

            // 用内置示例模型当作设备返回的 model.json
            return EmbeddedSampleModel.Json;
        }
    }
}

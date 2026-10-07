using NcLink.Protocol;
using NcLink.Protocol.Contracts;
using NcLink.Client.Files.Engine;
using NcLink.Client.Files.Models;
using NcLink.Client.Connection.Exceptions;
using NcLink.Client.Query.Engine;
using NcLink.Client.Query.Models;
using NcLink.Client.Query.Interfaces;
using NcLink.Client.Set.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;

namespace NcLink.Tests.Files;

/// <summary>
/// 文件传输单元测试：<b>不需要 MQTT Broker</b>。
///
/// 之所以能做到，是因为 <see cref="FileTransfer"/> 只依赖窄接口
/// <see cref="IFileNodeAccessor"/>——用假 accessor 就能精确控制
/// "设备返回什么"，从而验证分块、偏移推进、十六进制与 GB2312 编解码。
/// </summary>
public sealed class FileTransferTests
{
    private const string Device = "TEST_DEVICE";
    private static readonly string FilePath = NcLinkProtocolPaths.File;

    #region 列表 / 属性

    [Fact]
    public async Task ListFiles_ShouldReturnDeviceKeys()
    {
        var fake = new FakeAccessor();
        fake.OnQuery = (_, operation, _, _, _) =>
        {
            Assert.Equal(NcLinkOperation.GetKeys, operation);
            return FakeAccessor.Values("O0001", "O0002", "AAA/OTEST");
        };

        var files = await new FileTransfer(fake, fake, NullLogger<FileTransfer>.Instance).ListFilesAsync(Device);

        Assert.Equal(["O0001", "O0002", "AAA/OTEST"], files);
    }

    [Fact]
    public async Task GetFileEntries_ShouldParseAttributes()
    {
        var fake = new FakeAccessor();
        fake.OnQuery = (_, operation, _, _, _) =>
        {
            Assert.Equal(NcLinkOperation.GetAttributes, operation);
            // 设备按 changeTime / size / type 返回属性对象（camelCase，与模拟器一致）
            return new DataItemResult("01035430", true, null,
            [
                System.Text.Json.JsonDocument.Parse(
                    """{"changeTime":"2024/01/01 00:00:00","size":1024,"type":"file"}""").RootElement
            ]);
        };

        var entries = await new FileTransfer(fake, fake, NullLogger<FileTransfer>.Instance).GetFileEntriesAsync(Device, ["O0001"]);

        Assert.Single(entries);
        Assert.Equal("O0001", entries[0].Name);
        Assert.Equal(1024, entries[0].Size);
        Assert.Equal("file", entries[0].Type);
        Assert.Equal("2024/01/01 00:00:00", entries[0].ChangeTime);
    }
    #endregion

    #region 下载分块

    [Fact]
    public async Task Download_ShouldRequestBlocksAndAdvanceOffset()
    {
        // 12 字节，块大小 5 → 5 + 5 + 2 三次请求
        var content = "ABCDEFGHIJKL"u8.ToArray();
        var fake = new FakeAccessor();
        var requests = new List<(long? Offset, long? Length)>();

        fake.OnQuery = (_, operation, _, offset, length) =>
        {
            Assert.Equal(NcLinkOperation.GetValue, operation);
            requests.Add((offset, length));

            var chunk = content.AsSpan((int)offset!.Value, (int)length!.Value).ToArray();
            return FakeAccessor.Hex(FileTransfer.BytesToHex(chunk));
        };

        var bytes = await new FileTransfer(fake, fake, NullLogger<FileTransfer>.Instance).DownloadAsync(
            Device, "O0001", knownSize: content.Length, blockSize: 5);

        var trace = string.Join("\n", fake.Calls)
                    + $"\nbytes={bytes.Length} requests={requests.Count}";
        Assert.True(content.SequenceEqual(bytes), trace);
        Assert.True(requests.Count == 3, trace);
        Assert.Equal([(0L, 5L), (5L, 5L), (10L, 2L)], requests.Select(r => (r.Offset, r.Length)).ToArray());
    }
    [Fact]
    public async Task Download_WithoutKnownSize_ShouldResolveSizeFirst()
    {
        // 第一次请求是 get_attributes 取大小，之后才是 get_value
        var content = "M30"u8.ToArray();
        var fake = new FakeAccessor();
        var operations = new List<NcLinkOperation>();

        fake.OnQuery = (_, operation, _, offset, length) =>
        {
            operations.Add(operation);
            if (operation == NcLinkOperation.GetAttributes)
                return FakeAccessor.Objects(
                    """{"changeTime":"2024/01/01 00:00:00","size":3,"type":"file"}""");

            var chunk = content.AsSpan((int)offset!.Value, (int)length!.Value).ToArray();
            return FakeAccessor.Hex(FileTransfer.BytesToHex(chunk));
        };

        var bytes = await new FileTransfer(fake, fake, NullLogger<FileTransfer>.Instance).DownloadAsync(Device, "O0001", blockSize: 64);

        Assert.True(content.SequenceEqual(bytes), $"实际 {bytes.Length} 字节");
        Assert.Equal([NcLinkOperation.GetAttributes, NcLinkOperation.GetValue], operations);
    }

    [Fact]
    public async Task Download_ShortChunk_ShouldStopEarly()
    {
        // 声明 100 字节，但设备只给 3 字节就返回不足长度 → 立即停止，不再追问
        var fake = new FakeAccessor();
        var queryCount = 0;

        fake.OnQuery = (_, operation, _, _, _) =>
        {
            if (operation == NcLinkOperation.GetAttributes)
                return FakeAccessor.Objects("""{"changeTime":"2024/01/01 00:00:00","size":100,"type":"file"}""");

            queryCount++;
            return FakeAccessor.Hex(FileTransfer.BytesToHex("M30"u8.ToArray()));
        };

        var bytes = await new FileTransfer(fake, fake, NullLogger<FileTransfer>.Instance).DownloadAsync(Device, "X", blockSize: 10);

        Assert.True("M30"u8.ToArray().SequenceEqual(bytes), $"实际 {bytes.Length} 字节");
        Assert.Equal(1, queryCount);   // 只请求了一次，没有死循环
    }

    [Fact]
    public async Task Download_DeviceReturnsMoreThanRequested_ShouldTruncateNotOverflow()
    {
        // 声明 4 字节，但设备（违约）一口气返回 10 字节。
        // 必须截断——直接写进按总长预分配的缓冲会越界。
        var fake = new FakeAccessor();

        fake.OnQuery = (_, operation, _, _, _) => operation == NcLinkOperation.GetAttributes
            ? FakeAccessor.Objects("""{"changeTime":"2024/01/01 00:00:00","size":4,"type":"file"}""")
            : FakeAccessor.Hex(FileTransfer.BytesToHex("ABCDEFGHIJ"u8.ToArray()));

        var bytes = await new FileTransfer(fake, fake, NullLogger<FileTransfer>.Instance).DownloadAsync(Device, "X", blockSize: 4);

        Assert.Equal(4, bytes.Length);
        Assert.Equal("ABCD"u8.ToArray(), bytes);
    }

    [Fact]
    public async Task Download_ZeroSizeFile_ShouldReturnEmptyWithoutReading()
    {
        var fake = new FakeAccessor();
        fake.OnQuery = (_, operation, _, _, _) => operation == NcLinkOperation.GetAttributes
            ? FakeAccessor.Objects("""{"changeTime":"2024/01/01 00:00:00","size":0,"type":"file"}""")
            : throw new InvalidOperationException("零字节文件不应发起读取");

        var bytes = await new FileTransfer(fake, fake, NullLogger<FileTransfer>.Instance).DownloadAsync(Device, "X");

        Assert.Empty(bytes);
    }

    [Fact]
    public async Task Download_DeviceRejected_ShouldThrow()
    {
        var fake = new FakeAccessor();
        fake.OnQuery = (_, operation, _, _, _) => operation == NcLinkOperation.GetAttributes
            ? FakeAccessor.Objects("""{"changeTime":"2024/01/01 00:00:00","size":10,"type":"file"}""")
            : new DataItemResult(FilePath, false, "文件被锁定", []);

        var exception = await Assert.ThrowsAsync<NcLinkDeviceRejectedException>(() =>
            new FileTransfer(fake, fake, NullLogger<FileTransfer>.Instance).DownloadAsync(Device, "X", blockSize: 10));

        Assert.Equal(Device, exception.DeviceId);
        Assert.Contains("锁定", exception.Reason);
    }

    [Fact]
    public async Task Download_FileWithNoAttributes_ShouldThrowInsteadOfReturningEmpty()
    {
        // 回归：设备对不存在的文件返回 null（而不是属性对象）。
        // 曾把"取不到属性"伪造成 size=0 的条目，于是下载出空内容、上层误报成功——
        // 而官方同一场景返回"取不到文件大小"错误。0 字节文件走的是
        // Download_ZeroSizeFile_ShouldReturnEmptyWithoutReading 那条（返回对象且 size=0）。
        var fake = new FakeAccessor();
        fake.OnQuery = (_, operation, _, _, _) => operation == NcLinkOperation.GetAttributes
            ? FakeAccessor.Objects("null")
            : throw new InvalidOperationException("取不到大小时不应发起读取");

        await Assert.ThrowsAsync<NcLinkProtocolException>(() =>
            new FileTransfer(fake, fake, NullLogger<FileTransfer>.Instance).DownloadAsync(Device, "X"));
    }

    [Fact]
    public async Task GetFileEntries_NoAttributes_ShouldReturnEmpty()
    {
        var fake = new FakeAccessor();
        fake.OnQuery = (_, _, _, _, _) => FakeAccessor.Objects("null");

        var entries = await new FileTransfer(fake, fake, NullLogger<FileTransfer>.Instance)
            .GetFileEntriesAsync(Device, ["X"]);

        Assert.Empty(entries);
    }
    #endregion

    #region 下发分块

    [Fact]
    public async Task Upload_ShouldSendBlocksWithCorrectOffsetAndHex()
    {
        var content = "ABCDEFGHIJKL"u8.ToArray();
        var fake = new FakeAccessor();
        var sets = new List<(long? Offset, string? Hex)>();

        fake.OnSet = (_, operation, key, offset, value) =>
        {
            Assert.Equal(NcLinkOperation.SetValue, operation);
            Assert.Equal("O0009", key);
            sets.Add((offset, value));
            return true;
        };

        await new FileTransfer(fake, fake, NullLogger<FileTransfer>.Instance).UploadAsync(Device, "O0009", content, blockSize: 5);

        Assert.Equal(3, sets.Count);
        Assert.Equal(0L, sets[0].Offset);
        Assert.Equal(5L, sets[1].Offset);
        Assert.Equal(10L, sets[2].Offset);

        // 十六进制必须是<b>小写</b>（协议要求），且拼起来等于原文
        var joined = string.Concat(sets.Select(s => s.Hex));
        Assert.Equal(FileTransfer.BytesToHex(content), joined);
        Assert.Equal(joined, joined.ToLowerInvariant());
    }
    #endregion

    #region 编码往返

    [Fact]
    public async Task UploadText_ThenDownloadText_ShouldRoundTripChinese()
    {
        const string program = "%100\n(精加工中文注释)\nG54M03S2000\nM30\n%";

        var fake = new FakeAccessor();
        byte[]? uploaded = null;

        fake.OnSet = (_, _, _, _, value) =>
        {
            uploaded = FileTransfer.HexToBytes(value!);
            return true;
        };

        var transfer = new FileTransfer(fake, fake, NullLogger<FileTransfer>.Instance);
        await transfer.UploadTextAsync(Device, "O0001", program);

        Assert.NotNull(uploaded);
        Assert.Equal(program, Gb2312.Decode(uploaded!));

        // 再按同样的字节取回，验证解码一致
        fake.OnQuery = (_, operation, _, _, _) => operation == NcLinkOperation.GetAttributes
            ? FakeAccessor.Objects($$"""{"changeTime":"2024/01/01 00:00:00","size":{{uploaded!.Length}},"type":"file"}""")
            : FakeAccessor.Hex(FileTransfer.BytesToHex(uploaded!));

        var readback = await transfer.DownloadTextAsync(Device, "O0001");
        Assert.Equal(program, readback);
    }

    [Fact]
    public void HexRoundTrip_ShouldBeLowerCase()
    {
        var bytes = new byte[] { 0x00, 0xAB, 0x7F, 0xFF };
        var hex = FileTransfer.BytesToHex(bytes);

        Assert.Equal("00ab7fff", hex);
        Assert.Equal(bytes, FileTransfer.HexToBytes(hex));
        Assert.Equal(string.Empty, FileTransfer.BytesToHex([]));
    }

    [Fact]
    public void HexToBytes_OddLength_ShouldThrow()
    {
        Assert.Throws<FormatException>(() => FileTransfer.HexToBytes("abc"));
    }
    #endregion

    #region 目录操作

    [Fact]
    public async Task CreateDirectoryAndDelete_ShouldUseAddAndDeleteOperations()
    {
        var fake = new FakeAccessor();
        var operations = new List<(NcLinkOperation Op, string? Key)>();

        fake.OnSet = (_, operation, key, _, _) =>
        {
            operations.Add((operation, key));
            return true;
        };

        var transfer = new FileTransfer(fake, fake, NullLogger<FileTransfer>.Instance);
        Assert.True(await transfer.CreateDirectoryAsync(Device, "AAA"));
        Assert.True(await transfer.DeleteAsync(Device, "AAA/OTEST"));

        Assert.Equal([(NcLinkOperation.Add, "AAA"), (NcLinkOperation.Delete, "AAA/OTEST")], operations);
    }

    [Fact]
    public async Task InvalidBlockSize_ShouldThrow()
    {
        var fake = new FakeAccessor();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            new FileTransfer(fake, fake, NullLogger<FileTransfer>.Instance).UploadAsync(Device, "X", [1, 2, 3], blockSize: 0));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            new FileTransfer(fake, fake, NullLogger<FileTransfer>.Instance).UploadAsync(Device, "X", [1, 2, 3],
                blockSize: FileTransferLimits.MaxBlockSize + 1));
    }
    #endregion

    #region 测试替身

    /// <summary>GB2312 编码（测试侧独立实现，避免依赖被测代码）</summary>
    private static class Gb2312
    {
        private static readonly System.Text.Encoding Encoding = Create();

        private static System.Text.Encoding Create()
        {
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
            return System.Text.Encoding.GetEncoding("GB2312");
        }

        public static string Decode(byte[] bytes) => Encoding.GetString(bytes);
    }

    /// <summary>
    /// 假的文件节点访问器：记录调用参数，返回测试指定的响应。
    /// </summary>
    private sealed class FakeAccessor : IDataItemReader, IDataItemWriter
    {
        public Func<string, NcLinkOperation, string?, long?, long?, DataItemResult>? OnQuery { get; set; }

        public Func<string, NcLinkOperation, string?, long?, string?, bool>? OnSet { get; set; }

        /// <summary>调用记录（诊断用）。</summary>
        public List<string> Calls { get; } = [];

        public Task<DataItemResult> QueryItemAsync(
            string deviceId, DataItemRequest item, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        {
            var key = item.Keys is { Count: 1 } ? item.Keys[0] : null;
            var operation = item.Operation ?? NcLinkOperation.GetValue;

            Calls.Add($"Query op={operation} offset={item.Offset} length={item.Length} key={key}");

            var result = OnQuery?.Invoke(deviceId, operation, key, item.Offset, item.Length) ?? Values();

            Calls.Add($"  -> succeeded={result.Succeeded} values={result.Values.Count}"
                      + (result.Values.Count > 0 ? $" raw={result.Values[0].GetRawText()}" : string.Empty));

            return Task.FromResult(result);
        }

        public Task<bool> SetItemAsync(
            string deviceId, DataItemRequest item, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        {
            var key = item.Keys is { Count: 1 } ? item.Keys[0] : null;
            var value = item.Value as string;
            var ok = OnSet?.Invoke(deviceId, item.Operation ?? NcLinkOperation.SetValue, key, item.Offset, value) ?? true;
            return Task.FromResult(ok);
        }

        /// <summary>构造一个"成功且带字符串值"的结果（用于文件名的十六进制内容）。</summary>
        public static DataItemResult Values(params string[] values)
            => new(FilePath, true, null,
                values.Select(v => System.Text.Json.JsonSerializer.SerializeToElement(v)).ToArray());

        /// <summary>
        /// 构造一个"成功且值为 JSON 对象"的结果（用于 get_attributes）。
        ///
        /// 注意与 <see cref="Values"/> 的区别：文件属性必须是 JSON <b>对象</b>，
        /// 直接序列化字符串会得到转义后的 JSON 字符串，属性就取不出来了。
        /// </summary>
        public static DataItemResult Objects(params string[] jsonObjects)
            => new(FilePath, true, null,
                jsonObjects.Select(j => System.Text.Json.JsonDocument.Parse(j).RootElement).ToArray());

        /// <summary>构造一个"成功且值为十六进制串"的结果。</summary>
        public static DataItemResult Hex(string hex)
            => Values(hex);
    }
}
    #endregion

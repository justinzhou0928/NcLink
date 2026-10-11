# NC-Link C# 客户端

用 C# 直接与接入 NC-Link 的数控机床通信，**跳过官方的 Java apiServer**。

[![build](https://github.com/your-org/nclink/actions/workflows/build.yml/badge.svg)](./.github/workflows/build.yml)

```
本库 (C#)  ──MQTT/NC-Link JSON──>  EMQX  ──MQTT──>  数控机床(华中8型) 或 模拟机床
```

官方方案是 `MES → apiServer(Java, HTTP) → EMQX → 机床`。本项目把 apiServer 那段用 C# 实现，
对上层暴露 C# 异步 API，对下直接说 MQTT 上的 NC-Link 协议。

## 包

| 包 | 说明 |
|---|---|
| **NcLink** | NC-Link 协议的全部实现：领域模型、协议报文、客户端编排、MQTT 传输、DI 扩展 |
| **NcLink.Simulator** | 设备侧协议参考实现，可离线联调（可执行，非包） |
| **NcLink.ApiServer** | 官方 apiServer 的 C# 可替换实现（可执行，非包） |

**为什么只有一个库包**：NC-Link 协议只有 MQTT 一种传输（协议就定义在 MQTT + JSON 之上），
不存在"多协议共用抽象、需要可插拔驱动"的场景。
解耦点是 `INcLinkTransport` **接口**而不是程序集边界——它让单元测试用内存假实现即可运行。

## 安装

```powershell
dotnet add package NcLink
```

## 快速开始

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NcLink.Client;
using NcLink.Client.Query.Models;
using NcLink.Extensions;
using NcLink.Transport.Mqtt;

// 组合根：注册到容器。客户端相关组件全部单例，由容器装配。
var builder = Host.CreateApplicationBuilder(args);

// Broker 地址、凭据、重连策略属于传输专有设置
builder.Services.AddNcLinkMqtt(o => o.BrokerAddress = "tcp://127.0.0.1:1883");
// 传输已注册，这里不用再传 transportFactory
builder.Services.AddNcLink(o => o.RequestTimeout = TimeSpan.FromSeconds(3));
builder.Services.AddNcLinkHostedService();   // 随宿主启动连接、随宿主停止释放

using var host = builder.Build();
await host.StartAsync();

// 按能力注入，只依赖真正需要的那部分
var client = host.Services.GetRequiredService<INcLinkClient>();

// 探测设备，取回模型（路径解析依赖它）
var model = await client.ProbeAsync("STRESS_TEST_00000");

// 按路径或标识查询
var status = await client.QueryItemAsync("STRESS_TEST_00000", DataItemRequest.At("/MACHINE/STATUS"));
int value = status.IntAt(0);

// 查寄存器区间
var registers = await client.QueryItemAsync("STRESS_TEST_00000",
    DataItemRequest.At("/MACHINE/CONTROLLER/VARIABLE@REG_X", 0, 9));
int[] values = registers.ToIntArray();

// 设值 + 位操作
await client.SetItemAsync("STRESS_TEST_00000",
    DataItemRequest.Set("/MACHINE/CONTROLLER/VARIABLE@REG_X", 123, index: 10));
await client.SetItemAsync("STRESS_TEST_00000",
    DataItemRequest.Set("/MACHINE/CONTROLLER/VARIABLE@REG_X", 1, index: 0, bitOffset: 3));

// 采样订阅（事件回调，无需轮询）
var subscription = await client.SubscribeSampleAsync("STRESS_TEST_00000", "sample_channel0");
subscription.FrameReceived += (_, frame) =>
{
    foreach (var (itemId, data) in frame.Data)
        Console.WriteLine($"{itemId} = [{string.Join(", ", data)}]");
};

// G 代码文件（GB2312 + 十六进制 + 自动分块）
var text = await client.Files.DownloadTextAsync("STRESS_TEST_00000", "O0001");
await client.Files.UploadTextAsync("STRESS_TEST_00000", "MYPROG", "%1\nM30\n%\n");

await host.StopAsync();
```

> **不要手工 `new NcLinkClient(...)`。** 门面的构造函数形参是 internal 类型，
> 只应由容器解析（注册后 `NcLinkClient` 也可直接注入）。
> 全部组件由 `AddNcLink` 单例注册，依赖写在各自构造函数签名上。

### 依赖注入

`NcLink` 自带两个 DI 扩展：`AddNcLink(...)` 注册协议与客户端，
`AddNcLinkMqtt(...)` 注册 MQTT 传输。两者都是同一个包里的扩展方法。

```csharp
services.AddLogging();

// 方式一：代码配置
services.AddNcLinkMqtt(o => o.BrokerAddress = "tcp://127.0.0.1:1883");
services.AddNcLink(o => o.RequestTimeout = TimeSpan.FromSeconds(3));

// 方式二：绑定配置文件的节（"NcLink:Mqtt" / "NcLink"）
services.AddNcLinkMqtt(configuration.GetSection(MqttTransportOptions.SectionName));
services.AddNcLink(configuration.GetSection(NcLinkClientOptions.SectionName));

// 之后按能力注入，只依赖真正需要的那部分
public sealed class MyService(IQuery query)
{
    public Task<DataItemResult> ReadStatusAsync(string sn)
        => query.QueryItemAsync(sn, DataItemRequest.At("/MACHINE/STATUS"));
}
```

可注入的能力接口：`INcLinkClient`（门面，含全部能力与连接状态）、
`IQuery`（官方 5.1 查询 + 探测 + 采样）、`ISet`（官方 5.1 设置）、
`IFileTransfer`（官方 5.2 文件）、`INcLinkTransport`（传输，可替换）。

注册策略：一律**单例**——它们共享同一条 MQTT 连接与关联表；
注册成瞬时会让每个请求各建一套关联表，请求永远等不到响应。

**若不用 MQTT 传输**（例如自定义传输或测试用的内存传输），
跳过 `AddNcLinkMqtt`，改为注册自己的 `INcLinkTransport`：

```csharp
services.AddSingleton<INcLinkTransport>(new MyTransport());
services.AddNcLink(o => o.RequestTimeout = TimeSpan.FromSeconds(3));
```

#### 接入 .NET 宿主（自动连接 / 自动释放）

```csharp
services.AddNcLinkMqtt(o => o.BrokerAddress = "tcp://127.0.0.1:1883");
services.AddNcLink(o => o.RequestTimeout = TimeSpan.FromSeconds(3));
services.AddNcLinkHostedService();   // 随宿主启动连接，随宿主停止释放
```

连接失败**不会阻断宿主启动**——记录警告后由传输层的重连循环继续尝试，
因此 EMQX 晚启动或网络抖动不会让整个应用起不来。

### 异常

| 异常 | 含义 |
|---|---|
| `NcLinkTimeoutException` | 设备出现过但本次未响应 |
| `NcLinkDeviceUnavailableException` | 设备从未出现／SN 错误（比"超时"更可操作） |
| `NcLinkModelPathNotFoundException` | 路径／标识在模型里不存在 |
| `NcLinkDeviceRejectedException` | 设备返回 `NG` |
| `NcLinkInvalidRequestException` | 模型尚未加载，需先 `ProbeAsync`/`GetModelAsync` |
| `NcLinkNotConnectedException` | 传输未连接 |

## 离线联调

没有真机时用内置模拟器：

```powershell
# 终端 1：起 4 台模拟机床
dotnet run --project NcLink.Simulator -- --count 4

# 终端 2：跑集成测试（会自己启动模拟设备）
dotnet test

# 只做离线模型/路径校验，不需要 Broker
dotnet test --filter 'FullyQualifiedName~DeviceModelTests'
```

模拟器参数：

| 参数 | 说明 |
|---|---|
| `--host` | MQTT Broker 地址（默认 `127.0.0.1`） |
| `--port` | MQTT 端口（默认 `1883`） |
| `--user` / `--password` | Broker 凭据（未启用认证时留空） |
| `--count` | 模拟设备台数（默认 2） |
| `--prefix` | 设备 SN 前缀（默认 `STRESS_TEST_`，配合 `--count` 生成 `...00000` 等） |
| `--sn` | 显式指定 SN，逗号分隔（优先于 `--prefix` / `--count`） |
| `--channel-suffix` | 采样通道 id 追加 SN 后缀，避免多台设备通道同名 |
| `--no-echo-id` | 响应不回显 `@id`（用于验证客户端的降级匹配路径） |
| `--pid-dir` | 写 `{SN}.pid` 的目录，便于按设备精确终止进程 |

## 项目结构

```
NcLink/
├── NcLink/                        核心包（顶层按功能域分）
│   ├── NcLinkException.cs         异常层次（库级公开契约）
│   ├── NcLinkClient.cs            门面（只做装配）+ INcLinkClient
│   ├── ServiceCollectionExtensions.cs   依赖注入注册 AddNcLink(...)
│   ├── NcLinkHostedService.cs     宿主集成
│   ├── Protocol/                  线上格式（域 10 个文件 → 域内按作用分层）
│   │   ├── Interfaces/            INcLinkCodec / INcLinkTopics / INcLinkTransport
│   │   ├── Models/                NcLinkEnvelope / NcLinkMessages / NcLinkOperation / NcLinkProtocolPaths
│   │   └── Engine/                JsonNcLinkCodec / NcJsonValue / NcLinkTopics
│   ├── Logging/                   各组件一个文件，全部 [LoggerMessage] 都在这里
│   └── Client/                    客户端（按官方手册功能面划分）
│       ├── Connection/            机制：连接、请求关联、配置（扁平）
│       ├── Devices/               官方第 8 章「数据字典」（扁平）
│       ├── Files/                 官方 5.2「获取/下发文件」（扁平）
│       ├── Set/                   官方 5.1「设置」（扁平）
│       └── Query/                 官方 5.1「查询」+ 采样（域 11 个文件 → 域内按作用分层）
│           ├── Interfaces/        IQuery / IDataItemReader / ISampling / ISampleSubscription
│           ├── Models/            DataItemRequest / DataItemResult / SampleFrame
│           └── Engine/            DataItemReader / DataItemMessages / SampleSubscriptions / SampleMultiplexer
├── Transport/                     传输实现
│   └── Mqtt/                      MqttTransport / MqttTransportOptions / AddNcLinkMqtt(...)
├── NcLink.Simulator/              设备侧参考实现
├── NcLink.ApiServer/              官方 apiServer 的 C# 可替换实现
├── NcLink.Conformance/            与官方 apiServer 的一致性验证工具
├── tools/check-structure.py       结构审查（命名空间一致性 + 文件夹性质 + 编码约定）
├── NcLink.Tests/                  单元测试（不需要 Broker）+ 真实 MQTT 集成测试
│   ├── Client/  Model/  Protocol/  Files/  Transport/  Extensions/  Fakes/
│   └── Integration/               真实 MQTT + 进程内模拟设备
└── docs/                          实测记录与契约说明
```

每个文件的命名空间与它的文件夹一致。

**顶层按功能域分；域够大时，域内再按作用分。** 判据有两条，都要满足：

1. 该域的 `.cs` 文件数 **>= 8**
2. 分层后每个作用文件夹 **>= 2** 个文件

两条都满足才分层，否则保持扁平。**不给单个文件开文件夹。**

这个仓库曾经把**每个**域都切成四层，结果 57 个文件散在 **24 个文件夹**里、
**11 个只装 1 个文件**；但一刀切全扁平也不对——`Query`(11) 和 `Protocol`(10)
这么大的域，按作用分层确实好找。把门槛写成统一判据后，**规则会随代码规模自己生效**。

结构与命名空间由脚本守住：`python tools/check-structure.py`（退出码 0 = 通过）。

### 依赖方向

```
NcLink.Tests ──┐
NcLink.ApiServer ──┼──> NcLink ──> Microsoft.Extensions.*（抽象包）+ MQTTnet
NcLink.Conformance ┘
NcLink.Simulator ──────> NcLink（走自己的设备侧收发）
```

**解耦点是 `INcLinkTransport` 接口，不是程序集边界。**
MQTT 实现在 `NcLink` 包内的 `Transport/Mqtt/`——NC-Link 协议只有 MQTT 一种传输。
真正让绝大多数单元测试在**没有 Broker** 的情况下毫秒级跑完的，是这个接口 +
`FakeTransport`：把传输换成内存假实现即可，与 MQTTnet 在不在同一个程序集无关。

`NcLink` 只引 `Microsoft.Extensions.*` 的**抽象包**（DI/日志/配置/选项），
唯一的实现依赖是 MQTTnet——协议本身就定义在 MQTT + JSON 之上。

## 协议依据

| 来源 | 内容 |
|---|---|
| 《HNC8-NOM 接口参考手册》 | 设备端 MQTT 报文格式、主题、G 代码十六进制传输 |
| 《NC-Link 应用开发指导手册 0.3.10》 | apiServer 的 HTTP 接口面、数据字典、状态码 |
| 官方 `nclink-core-2.1.3.jar` 常量 | 路径分隔符 `/` 与 `@`、主题前缀、操作枚举、返回码 |
| GB/T 41970-2022 | 智能工厂数控机床互联接口规范（NC-Link 的国标形态） |

### 主题表

| 主题 | 用途 |
|---|---|
| `Probe/Query/Request/{SN}` → `Probe/Query/Response/{SN}` | 取设备模型（model.json） |
| `Query/Request/{SN}` → `Query/Response/{SN}` | 取值（get_value / get_keys / get_attributes / get_length） |
| `Set/Request/{SN}` → `Set/Response/{SN}` | 设值 / 位操作 / 文件操作（set_value / add / delete） |
| `Sample/{deviceId}/{channelId}` | 采样数据推送 |
| `Register/Request` | 设备注册（字段名是**小写** `deviceid`） |

`{SN}` 即设备 SN 码（面板「维护 → 注册」可查）。

### 路径解析

```
/NC_LINK_ROOT/MACHINE/CONTROLLER/VARIABLE@REG_X   ← 完整路径
/MACHINE/CONTROLLER/VARIABLE@REG_X                ← 去根前缀
VARIABLE@REG_X                                    ← 标识
01035430                                          ← 直接给 id
```

以上写法都能解析到同一个数据项。

## 关键设计决策

### 请求-响应关联：两层匹配，顺序不可颠倒

1. **严格匹配**：报文体出现 `"@id":"<本次关联 id>"`，或调用方给出的强特征；
2. **降级匹配**：仅当无任何严格匹配命中时启用，且必须先通过
   `INcLinkTopics.IsResponseFor` 校验响应主题归属。

早期实现用「报文里出现设备 SN」做兜底，而 SN 也出现在响应主题里，
导致掉线设备的**迟到响应被后续请求领走**（表现为"已掉线设备仍返回成功"）——
把过期数据当成本次结果，生产上极难排查。`Correlation/RequestCorrelatorTests.cs` 专门守着这条。

### 采样归因

采样主题是 `Sample/{deviceId}/{channelId}`（**三层，含设备 SN**），
因此归属是确定的，**不存在"通道 id 歧义"**——多台设备可以安全地共用同一个通道 id。

> 曾有一版实现把主题当成两层 `Sample/{channelId}`，并据此设计了"多设备共用通道 id 时
> 丢弃该帧"的猜测式归因。那是错的，会导致订阅后永远收不到帧。
> 详见 [AGENTS.md](AGENTS.md) §4 与 [CHANGELOG.md](CHANGELOG.md)。

## 用于真实机床前必须确认

1. **设备是否回显 `@id`**：用 `--no-echo-id` 可模拟不回显的情况；
   建议现场先用 `mosquitto_sub -t '#' -v` 抓一次真实报文确认。
2. **编解码方式**：默认明文 JSON；若真机使用其它编码，实现 `INcLinkCodec` 替换即可。
3. **设备 SN 与网络**：机床侧 `/h/lnc8/bin/nclink.cfg` 的 `server_ip`/`server_port`
   须指向你的 EMQX，改完重启机床；数控系统 2.40.04 及以上还需把**参数 50 号设为 2**。
4. **采样通道定义**：`SAMPLE_CHANNEL` 节点的 `ids` 列表决定通道内容，随系统版本可能不同。

## 测试

```powershell
dotnet test     # 120 个单元（不需要 Broker）+ 8 个真实 MQTT 集成
```

集成测试在**进程内**启动模拟机床；本机 1883 端口没有 Broker 时自动跳过而非失败。

## 许可

[MIT](./LICENSE)

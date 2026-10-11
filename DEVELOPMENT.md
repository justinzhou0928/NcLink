# 开发说明

> **文档边界**
> - [README.md](README.md) —— 使用者：安装、快速开始、协议依据
> - [DEVELOPMENT.md](DEVELOPMENT.md)（本文）—— 人开发者：项目划分、测试组织、设计约定
> - [AGENTS.md](AGENTS.md) —— AI Agent：硬性规则、必跑检查、操作纪律
>
> 本项目有 AI Agent 参与开发，因此把"不可违反的规则"单独抽到 `AGENTS.md`，
> 避免它们散落在长文里被忽略。


## 环境要求

- .NET SDK 10.0 或更高（`Directory.Build.props` 统一指定 `TargetFramework=net10.0`）
- 一个 MQTT Broker（可选，仅集成测试需要）：EMQX 或 Mosquitto，默认 `127.0.0.1:1883`
  - 没有 Broker 时集成测试会**自动跳过**而非失败，因此日常开发不必先起 Broker

## 常用命令

```powershell
dotnet build                              # 编译全部项目
dotnet test                               # 全部测试（不要写死数量）
dotnet test --filter 'FullyQualifiedName~DeviceModelTests'   # 只跑某类测试
dotnet run --project NcLink.Simulator -- --count 4 --pid-dir "$env:TEMP\nclink"  # 起模拟设备
python tools/check-structure.py            # 结构 + 编码约定审查（退出码 0 = 通过）
python tools/test-check-structure.py       # 审查脚本自身的自测（改了脚本才需跑）
```

**能脚本判定的约定一律加进 `check-structure.py`，不要只写进文档。**
原因：`// ----` 分割线、`-Service` 泛化后缀、嵌套日志类、region 缩进
这几类违规连续出现过多次，每次都靠人工发现——**写入文档对执行者不构成约束**。
现在的 [5] 段会直接让审查失败：

| 检查项 | 反例 |
|---|---|
| 禁止 `// ----` 注释分割线 | 分区一律用 `#region` / `#endregion` |
| 禁止 `-Service` 泛化后缀 | `ProbeService` → `DeviceProbe`；`HostedService` 按框架约定放行 |
| 公开接口成员必须写 XML 文档 | 少写就没法在 IntelliSense 里看到 |
| 日志类必须集中在 `Logging/` | 不得在组件内嵌套 `Log` 类 |
| `#region`/`#endregion` 缩进一致且闭合 | 缩进不一致或漏 `#endregion` |
| 不留 `TODO`/`FIXME`/`HACK` | 死代码与技术债直接处理掉 |
| csproj 必须用 `Microsoft.NET.Sdk` | 曾误写成 `Microsoft.NET.Sdk.Web`（把库当 Web 应用） |
| csproj 里不得有注释 | 说明写进 README/DEVELOPMENT；写在构建文件里必然与文档重复 |
| 行内注释块不超过 4 行 | 超过即为"把文档塞进注释"，说明应进 README/AGENTS |

## 项目划分

| 项目 | 职责 | 关键依赖 |
|---|---|---|
| `NcLink` | 领域模型、协议、客户端编排、MQTT 传输、DI 扩展 | `Microsoft.Extensions.*` **抽象包** + MQTTnet |
| `NcLink.Simulator` | 设备侧参考实现 | MQTTnet |
| `NcLink.Tests` | 单元 + 集成测试 | xunit、`Microsoft.Extensions.Logging`（实现） |
| `NcLink.Conformance` | 与官方 apiServer 的一致性验证工具 | — |
| `NcLink.ApiServer` | 官方 apiServer 的 C# 可替换实现 | ASP.NET Core（唯一的 Web 项目） |

**MQTT 传输（`Transport/Mqtt/`）为什么在核心包里**：NC-Link 协议只有 MQTT 一种传输，
不存在"多协议共用抽象、需要可插拔驱动"的场景。消费者现在只需引一个包。

关键在于**抽象仍在核心内部**：`INcLinkTransport` + `FakeTransport` 让 180 多个单测
脱离 Broker 运行，`NcLink.Simulator` 也能用核心包但走自己的设备侧收发。
**MqttTransport 与使用它的代码之间隔着接口，这才是解耦点，不是程序集边界。**

### 共享的构建属性

`Directory.Build.props` 统一了 `TargetFramework` / `LangVersion` / `Nullable` /
版本与包元数据，各 csproj 只保留引用与包特有设置。
**库项目若需要生成 XML 文档，在其自身 csproj 里打开 `GenerateDocumentationFile`**——
可执行项目与测试项目不需要，打开会产生大量 CS1591 警告。

### 目录组织约定

**顶层按功能域分；域够大时，域内再按作用分。**

两级判据，与代码规模挂钩：

1. **顶层按功能域分** —— 找东西时按"它是干什么的"去找。
2. **域内是否再按作用分（`Interfaces/` `Models/` `Parsers/` `Engine/` `Options/` `Exceptions/`）**：
   - 该域的 `.cs` 文件数 **>= 8**，**且**
   - 分层后每个作用文件夹 **>= 2** 个文件

   两条都满足才分层；否则保持扁平。**不给单个文件开文件夹。**

```
NcLink/
├── NcLinkException.cs                      异常层次（10 个类型，库级公开契约）
├── NcLinkClient.cs / INcLinkClient.cs      门面
├── ServiceCollectionExtensions.cs          依赖注入注册
├── NcLinkHostedService.cs                  宿主集成
├── Protocol/                               线上格式（域 10 个文件 → 分层）
│   ├── Interfaces/  INcLinkCodec / INcLinkTopics / INcLinkTransport
│   ├── Models/      NcLinkEnvelope / NcLinkMessages / NcLinkOperation / NcLinkProtocolPaths
│   └── Engine/      JsonNcLinkCodec / NcJsonValue / NcLinkTopics
├── Logging/                                集中式日志事件（5 个，扁平）
├── Transport/Mqtt/                         MQTT 传输实现（3 个，扁平）
└── Client/
    ├── Connection/   机制：连接、请求关联、配置（6 个，扁平）
    ├── Devices/      官方第 8 章「数据字典」（9 个，扁平）
    ├── Files/        官方 5.2「获取/下发文件」（4 个，扁平）
    ├── Set/          官方 5.1「设置」（3 个，扁平）
    └── Query/        官方 5.1「查询」+ 采样（域 11 个文件 → 分层）
        ├── Interfaces/  IQuery / IDataItemReader / ISampling / ISampleSubscription
        ├── Models/      DataItemRequest / DataItemResult / SampleFrame
        └── Engine/      DataItemReader / DataItemMessages / SampleSubscriptions / SampleMultiplexer
```

**为什么用规模当门槛，而不是"一律分"或"一律不分"**：

本仓库曾把**每个**域都切成 `Interfaces/` + `Models/` + `Parsers/` + `Engine/`，
结果 57 个文件散在 **24 个文件夹**里，其中 **11 个只装 1 个文件**——
`Client/Devices/Interfaces/` 里就 11 行，命名空间深到 5 段
（`NcLink.Client.Connection.Engine.RequestCorrelator`）。

但一刀切全扁平也不对：`Query`(11) 和 `Protocol`(10) 这么大的域，
按作用分层确实好找。

**把门槛写成统一判据后，规则会随代码规模自己生效**：库小的时候不许分，
某个域长大了自然放行——不需要每次靠讨论决定。
这条由 `check-structure.py` 的 `[2]` 强制：域 `< 8` 却分层、或某个作用文件夹
只有 1 个文件，都**硬失败**。项目根的直接子项（如 `NcLink.ApiServer/Models/`）不受此限。

**功能名以官方手册为准**：`Query` / `Set` 对应官方 5.1 的「查询」「设置」，
`Files` 对应官方 5.2 的「获取/下发文件」。
不要自创功能名——手册怎么叫，代码就怎么叫，读文档与读代码才对得上。

**机制不提升为功能域**。连接管理、请求关联、配置都是**实现手段**，
不是对外功能，因此统一收在 `Connection/` 下，而不是与 `Query`/`Set` 平级。

**异常层次放在库根**（`NcLinkException.cs`，命名空间 `NcLink`），不跟着机制走。
它是**库级公开契约**：8 个层里有 7 个引用它，且其中一半的类型
（`NcLinkInvalidRequestException` / `NcLinkModelPathNotFoundException` /
`NcLinkProtocolException`）跟"连接"无关。放库根还让传输层不必反向依赖
Client 层。.NET 惯例同此（`System.Text.Json.JsonException` 就在根命名空间）。

**一个功能域一个文件夹**：写入的实现属 `Set/`，不要塞进 `Query/`。

**关于可见性**：

- 同一个文件夹里 **public 与 internal 类型可以共存**——
  对外发布的是 public（`IQuery` / `IFileTransfer`），
  只给本程序集用的是 internal（`IRequestPipeline` / `DataItemOperations`）。
- **测试项目不适用可见性规则**：xUnit 只发现 public 测试类，测试代码本就是 public。

**命名空间必须与文件夹一致**（`Client/Query/Engine/` → `NcLink.Client.Query.Engine`）——
这条由 `check-structure.py` 的 `[1]` 强制。

### 命名：类型名说明"它是什么"，不用泛化后缀

各实现的命名遵循一条：**名字应能直接回答"这是什么"**。

| 好（说明是什么） | 差（什么也没说） |
|---|---|
| `DeviceModelCache` —— 是缓存 | `DeviceModelService` |
| `RequestCorrelator` —— 做关联 | `RequestService` |
| `SampleMultiplexer` —— 做多路分发 | `SampleService` |
| `DataItemReader` / `DataItemWriter` —— 读 / 写 | `QueryService`（读写都有，名字只提读） |
| `FileTransfer` —— 传文件 | `FileService` |
| `RequestPipeline` —— 是条管线 | `PipelineService` |
| `DeviceProbe` —— 做探测，对应 `IDeviceProbe` | `ProbeService` |

**实现类的名字应尽量对应它实现的接口**：`DeviceProbe : IDeviceProbe`、
`RequestCorrelator : IRequestCorrelator`、`DataItemReader : IDataItemReader`。
接口与实现在同一个命名空间里，靠名字本身区分（`IDeviceProbe` / `DeviceProbe`），
不靠前缀或后缀。

**判据不是"它是否做编排"，而是"名字能否回答这是什么"。**
做编排不构成使用泛化后缀的理由。本仓库因此不再有 `-Service` 后缀的实现类
（`NcLinkHostedService` 除外——它是 `IHostedService` 的实现，后缀由框架约定决定）。

两处真实反例：

| 原名 | 问题 | 改名 |
|---|---|---|
| `ProbeService` | 只有 1 个公开方法，做的是"发一次请求 → 解码 → 校验 → 返回"，即**一次协议往返** | `DeviceProbe` |
| `SamplingService` | **它确实做编排**（查重 → 确保连接 → 订阅 → 注册 → 注销 → 遍历通道），但它本质是**一份订阅清单**，自己就暴露 `Multiplexer` / `DroppedAmbiguousFrames`；"Service"只说它做什么、没说它是什么 | `SampleSubscriptions` |

这条约定来自一次真实的命名误导：原来的 `QueryService` **同时实现了查询与设置**
（4 个公开方法），名字却只说"查询"，导致 `Set/` 文件夹里只有接口没有实现。
拆分后是 `Query/DataItemReader` + `Set/DataItemWriter`，
功能面归属也随之正确。

**判断方法**：如果一个类需要 `-Service` 后缀才能说清它是干嘛的，
通常说明它承担了多个职责——先看能不能拆，而不是先起名。

### 依赖包约定

**只引抽象包，不引实现包。** `NcLink` 引的是
`DependencyInjection.Abstractions` / `Logging.Abstractions` / `Configuration.*` / `Options`——
这些都是接口与扩展方法，不含 DI 容器或日志实现的代码。
实现包（`Microsoft.Extensions.DependencyInjection`、`Microsoft.Extensions.Logging`）
只出现在**测试项目**里。

DI 扩展**放在核心包内**（根目录 `ServiceCollectionExtensions.cs`），
不单独建包——消费方引用核心包即可用 `services.AddNcLink(...)`。

**传输的 DI 扩展独立成一个方法**：`AddNcLinkMqtt(...)`
（`Transport/Mqtt/MqttServiceCollectionExtensions.cs`）。
同一个包内仍分成两个扩展，是因为只用内存传输做测试的调用方不必注册 MQTT；
这样 `AddNcLink` 也不需要 `transportFactory` 参数。

MQTTnet 5 没有 `IMqttClientFactory` 接口，`MqttTransport` 用 **`Func<IMqttClient>`
工厂委托**做注入点——这是 .NET 的标准手法，比为一个工厂自造接口更轻。

根目录 `NcLinkHostedService.cs` 提供宿主集成（`AddNcLinkHostedService()`），
只引 **`Microsoft.Extensions.Hosting.Abstractions`** 而非完整的
`Microsoft.Extensions.Hosting`（后者会拖进 DI 容器、配置实现、文件提供程序等）。
该抽象包已在用的 6 个 `Microsoft.Extensions.*` 之外只多 `Diagnostics.Abstractions`
与 `FileProviders.Abstractions`，因此不单独建包。

**连接失败不阻断宿主启动**是刻意设计：抛出去会让 EMQX 晚启动几秒就导致整个应用起不来；
恢复交给传输层的重连循环。但**取消异常不吞**——那是调用方意图。

命名空间与文件夹对齐：`NcLink.Protocol`、`NcLink.Device`、`NcLink.Client.*`。
新增文件时先判断"它属于哪个功能域"，再决定是否需要在域内建能力文件夹。

### 依赖方向的硬约束

```
NcLink.Tests ──────┐
NcLink.ApiServer ──┼──> NcLink ──> Microsoft.Extensions.*（抽象包）+ MQTTnet
NcLink.Conformance ┘
NcLink.Simulator ──────> NcLink（走自己的设备侧收发，不经 Transport/Mqtt）
```

**应用层代码（`NcLinkClient` 及其引擎组件）只依赖 `INcLinkTransport` 抽象**，
不引用 `Transport/Mqtt/` 的任何具体类型。这保证：

- 单元测试可用内存假传输（`NcLink.Tests/Fakes/FakeTransport.cs`）验证纯逻辑，毫秒级完成；
- 换传输实现（别的 MQTT 客户端、内存管道）无需触碰客户端逻辑。

**注意：这个约束靠的是接口，不是程序集边界。** MQTT 实现与核心在同一个包内——
NC-Link 协议只有 MQTT 一种传输，拆成独立程序集只会让消费者多引一个包，
却换不来任何实际的可替换性。判别标准是"`Client/` 里有没有直接 `using`
`NcLink.Transport.Mqtt`"，而不是"它们在不在同一个 dll"。

改动客户端代码时请保持这条约束。

## 优先用标准库，只对协议特有的东西自己实现

写代码前先确认「这是协议要求，还是我在造轮子」。本项目里**属于协议、必须自己实现**的只有：

| 自己实现的部分 | 为什么没有库可替代 |
|---|---|
| `RequestCorrelator` 的 `@id` 关联 + 两层匹配 | NC-Link 用 GUID 关联 MQTT 请求响应，无通用库 |
| `NcLinkTopics` 主题拼接 / 响应主题归属判定 | 协议规定 |
| `FileTransfer` 的十六进制 + 分块（50 KiB，上限 100 KiB） | 协议规定的文件传输格式 |
| `JsonNcLinkCodec` 的报文形状 | 协议规定的 JSON 结构 |
| `MqttTransport` 的指数退避重连 + 订阅恢复 | MQTTnet 5 移除了自动重连，约 20 行；引 Polly 换不来收益 |

其余都应当直接用 BCL / 成熟库，例如：

| 需求 | 用这个，别手写 |
|---|---|
| 字节 ↔ 十六进制 | `Convert.ToHexStringLower` / `Convert.FromHexString` |
| JSON 读写 | `System.Text.Json`（`JsonSerializer` / `JsonSerializer.SerializeToElement`） |
| GB2312 等代码页 | `Encoding.GetEncoding` + `CodePagesEncodingProvider` |
| 定时采样 | `PeriodicTimer` |
| 文件读写 | `File.ReadAllTextAsync` / `File.WriteAllBytesAsync` |
| 并发字典 | `ConcurrentDictionary` |
| 等待某一事件 | `TaskCompletionSource` + `WaitAsync(timeout)`，**不要用 `Task.Delay` 轮询** |

最后一条是踩过坑的：集成测试里曾用 `await Task.Delay(700)` 等采样帧，
负载高时会偶发失败。现在统一改成「等信号 + 超时」。

> 注意 `default(JsonElement)` 的 `ValueKind` 是 `Undefined`，直接序列化会抛
> `InvalidOperationException`。要产出 JSON `null` 请用
> `JsonSerializer.SerializeToElement<object?>(null)`。

## 测试组织

`NcLink.Tests` 用子文件夹镜像源码组织：

```
NcLink.Tests/
├── Device/          设备模型与路径解析、索引区间
├── Protocol/        主题规则、编解码、报文结构
├── Connection/      请求-响应关联（含迟到响应防误领）
├── Client/          数据服务、采样服务
├── Files/           文件传输（分块、偏移推进、GB2312、十六进制）
├── Extensions/      DI 注册与选项校验
├── Fakes/           FakeTransport、TestHarness
├── Integration/     真实 MQTT + 进程内模拟设备
└── AssemblyInfo.cs  关闭并行执行（集成测试会启动真实设备并让其掉线）
```

**优先写不需要 Broker 的单元测试。** `TestHarness` 复刻了生产代码的手工接线
（`RequestPipeline` → `DataItemReader` / `DataItemWriter` / `SampleSubscriptions`），配合 `FakeTransport`
就能对服务层做精确测试：注入设备响应、断言发出的报文、用毫秒级超时验证超时路径。

只有"必须验证真实 MQTT 行为"的场景才写集成测试（连接、重连、掉线隔离、
进程内模拟设备互操作）。判据是：**这个行为在 `FakeTransport` 上无法表达吗？**

### 两条必须遵守的测试纪律

1. **集成测试用独立设备前缀**。`BrokerFixture` 构造时可传前缀；
   `DiagnosticsTests` 会主动让设备掉线，若与其它测试类共用 SN，掉线状态会跨测试泄漏。
2. **新增纯逻辑必须配单元测试**。历史上出过的关联 bug 就是因为当时只能靠跑模拟器验证。

## 依赖注入：构造函数必填 + 容器装配，不手工 new

**本项目只走构造注入，不用服务定位器，不手工 `new` 组件。**

| 规则 | 原因 |
|---|---|
| 构造函数参数**必填**，不写 `ILogger? = null` / `TimeSpan? = null` | 可选依赖等于"可以静默漏传"，依赖关系在签名上不可见 |
| 组件注册用 `TryAddSingleton<T>()`，让容器解析构造函数 | 依赖链由容器推导，不用手写 `GetRequiredService` 链 |
| 生产代码里不出现 `new XxxService(...)` | 一旦手工装配，替换实现与测试替身都会失效 |
| 注册一律**单例** | 它们共享同一条连接与关联表；瞬时注册会让每个请求各建一套关联表，请求永远等不到响应 |

**需要 `ILogger` 时注入 `ILogger<T>` 或 `ILoggerFactory`，不要注入 `NullLogger`。**
测试里若手工构造，显式传 `NullLogger<T>.Instance`。

### 注册到容器的类型必须同时实现 IDisposable 与 IAsyncDisposable

`ServiceProvider.Dispose()`（同步释放）遇到**只实现 `IAsyncDisposable`** 的单例会
直接抛 `InvalidOperationException`，而
`using var provider = services.BuildServiceProvider()` 是消费者很自然会写的代码。

因此凡是注册为单例、由容器持有并释放的类型（`NcLinkClient`、`INcLinkTransport` 及其实现），
都提供两种释放，其中一个转调另一个：

```csharp
public async ValueTask DisposeAsync() { /* 真正的释放 */ }

public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
```

`INcLinkTransport` 契约已把两者都写进接口，**新的传输实现必须一并提供**。

**两个例外**（都在 `AddNcLinkCore` 内，有注释说明）：

1. **门面 `NcLinkClient`**：构造函数形参含 internal 类型，因此是 internal，
   容器无法按类型注册，只能显式列依赖构造。
2. **需要 `IOptions<T>` 的值类型**（`NcLinkClientOptions` / `CorrelationOptions`）：
   `TryAddSingleton(sp => sp.GetRequiredService<IOptions<T>>().Value)` 把配置解析成单例。

### 双向接线必须显式写出来

容器只能表达"谁依赖谁"，表达不了"构造后把 A 的回调接到 B"。
本项目有一处这种接线：

```csharp
// RequestPipeline 收到采样报文 → 交给 SampleMultiplexer 分发
_pipeline.ForwardSamplesTo(message => samples.TryDispatch(message));
```

它写在 `NcLinkClient` 构造函数里。**改动这一带时务必保留**——
曾经因为改造遗漏这一行，导致采样帧无人分发、33 个测试失败。

## 长驻循环：用 StartAsync/IAsyncDisposable，不用 BackgroundService

**`BackgroundService` 是宿主（`IHost`）的托管原语，不是库组件的形态。**
判断依据：

| 维度 | `BackgroundService` | 本项目的长驻循环 |
|---|---|---|
| 启动方式 | 宿主自动 `StartAsync` | 库使用者显式 `StartAsync` |
| 依赖 | 需要 `Microsoft.Extensions.Hosting` | 只依赖抽象包 |
| 生命周期 | 一个宿主服务一个实例 | `MqttTransport` 一个重连循环；`SimulatedMachine` **N 台机器 N 个采样循环** |
| 结束方式 | `ExecuteAsync` 返回后由宿主判定 | 由 `IAsyncDisposable` 停止 |

具体到两个类：

- **`MqttTransport`**：库不应该强制使用者跑 `IHost` 才能建一个传输。
  它提供 `ConnectAsync` + `IAsyncDisposable`，重连循环内部用 `Task.Run` 起，
  释放时 await 收尾。使用者若在 `IHost` 里想要自动启动，
  **自己包一层 `BackgroundService` 调用 `ConnectAsync`** 即可——那是应用侧的组装选择。
- **`SimulatedMachine`**：每台模拟机床一条采样循环，
  若改成 `BackgroundService` 就要注册 N 个实例，比 `StartAsync`/`StopAsync` 更绕。

**`DeviceProbe` 之类不是循环。** 它们被调用才干活（请求驱动），
`BackgroundService` 对它没有任何意义——没有循环可跑。

**若真的用了 fire-and-forget 的循环，必须观测异常。**
`SimulatedMachine` 的采样循环是故意不 await 的，因此接了一个 continuation
把非取消异常打到 stderr——否则"看起来在跑但不再发采样"，极难排查。

## 缓存：按“数据是否可淘汰”选型，不默认用 IMemoryCache

`DeviceModelCache` 用 `ConcurrentDictionary` + `Lazy<Task<T>>` 而**不是**
`Microsoft.Extensions.Caching.Memory`。判断依据：

| 维度 | 设备模型 | `IMemoryCache` 的假设 |
|---|---|---|
| 是否可淘汰 | **不可**——淘汰后下次请求要重新探测（一次 MQTT 往返） | 可淘汰，带过期与容量上限 |
| 数量级 | 等于设备台数（几台到几十台） | 无界，需要容量控制 |
| 数据是否可变 | 不可变，只需"取一次" | 通常会被更新 |
| 真正需要的能力 | **并发去重**（多次请求只探测一次） | 过期/淘汰/`GetOrCreateAsync` |

需要的是并发去重：`GetOrAdd` 一个 `Lazy<Task<DeviceModel>>`，
让同一设备的并发调用共享同一个任务。这**不需要新依赖**，
而 `IMemoryCache` 的 `GetOrCreateAsync` 并不保证只执行一次工厂方法。

**引入缓存库前先问：这个数据能被淘汰吗？** 不能淘汰的数据用字典，
能淘汰的才用缓存库。

## 关键设计约定

- **请求-响应关联**：严格匹配（`@id` 或调用方强特征）优先；
  降级匹配必须先用 `INcLinkTopics.IsResponseFor` 校验响应主题归属。
  顺序不可颠倒，否则并发请求会互相认领响应。
- **探测不做弱特征兜底**：宁可超时，也不认领可疑响应。
- **采样归因**：通道 id 在多台设备间重复时丢弃该帧，不猜测归属。
- **传输层只搬字节**：`INcLinkTransport` 不出现报文类型，序列化归 `INcLinkCodec`。
- **异常语义**：设备从未出现过报 `DeviceUnavailableException`；
  出现过但本次没回应报 `NcLinkTimeoutException`。

## 协议参考

| 来源 | 内容 |
|---|---|
| 《HNC8-NOM 接口参考手册》 | 设备端 MQTT 报文格式、主题、G 代码十六进制传输 |
| 《NC-Link 应用开发指导手册 0.3.10》 | apiServer 的 HTTP 接口面、数据字典、状态码 |
| 官方 `nclink-core-2.1.3.jar` 常量 | 路径分隔符 `/` 与 `@`、主题前缀、操作枚举、返回码 `OK`/`NG` |
| GB/T 41970-2022 智能工厂数控机床互联接口规范 | 国家标准（NC-Link 的国标形态） |

---

## NcLink.ApiServer

官方 apiServer 的 C# 可替换实现，是本仓库唯一的 Web 项目（`Microsoft.NET.Sdk.Web`）。
它只做两件事：HTTP 形状 ↔ 库调用的编解码，以及服务端特有的采样缓存
（`Operations/SampleCache.cs`）。**协议逻辑一律在 `NcLink` 库里，不要搬进这里。**

```powershell
dotnet run --project NcLink.ApiServer                                  # 默认 19001
dotnet run --project NcLink.ApiServer -- --ApiServer:Port=19002        # 并列跑
```

契约、错误码、采样语义的实测依据见 [docs/api-server.md](docs/api-server.md)。
与官方逐字符对照：

```powershell
dotnet run --project NcLink.Conformance -- --compare http://127.0.0.1:19002
```

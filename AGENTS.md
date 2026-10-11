# AGENTS.md

给 AI 编码 Agent 的硬性要求。**动手前先读完本文**；与本文冲突的做法一律不做。
人类开发者的入门说明见 [DEVELOPMENT.md](DEVELOPMENT.md)，使用者见 [README.md](README.md)。

---

## 0. 每次改完必跑（缺一不可）

```powershell
dotnet build --nologo                 # 必须 0 警告 0 错误
dotnet test  --nologo                 # 必须零失败（不要写死数量：加测试后数字会过期）
python tools/check-structure.py       # 必须退出码 0
```

**没跑完这三条就算没做完**，不要报告"已完成"。
`check-structure.py` 是结构约定的唯一裁决者——它报错时先判断是代码错还是规则错（见 §5）。

改了检查脚本本身时，还要跑它的自测：

```powershell
python tools/test-check-structure.py  # 必须全部通过（用例如今 16 个，不要写死数量）
```

**为什么有这条**：同一类违规（`// ----` 分割线、`-Service` 泛化后缀、
嵌套日志类、region 缩进）曾连续出现多次，都是靠人工发现。
**写进文档不构成约束，只有让 §0 的检查硬失败才有效。**
因此凡是"能脚本判定的约定"，一律加进 `check-structure.py` 的 [5] 段，
不要再指望靠记忆遵守。

---

## 1. 功能名与功能边界：以官方手册为准

协议功能面来自官方文档，**不要自创功能名或扩大范围**。

| 官方章节 | 对应代码 |
|---|---|
| 《应用开发指导手册》5.1「查询/设置相关参数」 | `Client/Query/`、`Client/Set/` |
| 《应用开发指导手册》5.2「获取/下发文件」 | `Client/Files/` |
| 《应用开发指导手册》第 8 章「数据字典」 | `Client/Devices/` |

手册表 5-1 列出的 `operation` 取值**就是全部**（7 个，与 `NcLinkOperation` 枚举一一对应）：
`get_value` / `set_value` / `get_length` / `get_keys` / `get_attributes` / `add` / `delete`。
新增操作前必须先在官方资料里找到出处。

**机制不提升为功能域**：连接管理、请求关联、配置、异常都是实现手段，
收在 `Client/Connection/` 下，不与 `Query`/`Set` 平级。

---

## 2. 目录与命名空间（有脚本强制）

**`Transport/` 装具体传输实现**（命名空间 `NcLink.Transport.Mqtt`，
与文件夹 `Transport/Mqtt/` 一致）。它和 `Client/` 之间隔着
`INcLinkTransport` 接口——**这才是解耦点，不是程序集边界**。

### 目录：按功能域分；域够大时，域内再按作用分（有脚本强制）

**两级判据，与代码规模挂钩：**

1. **顶层按功能域分** —— 找东西时按"它是干什么的"去找。
2. **域内是否再按作用分**，看规模：
   - 该域的 `.cs` 文件数 **>= 8**，**且**
   - 分层后每个作用文件夹 **>= 2** 个文件

   两条都满足才分层；否则保持扁平。**不给单个文件开文件夹。**

```
NcLink/
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
    ├── Connection/   机制：连接、请求关联、配置、异常（7 个，扁平）
    ├── Devices/      官方第 8 章「数据字典」（9 个，扁平）
    ├── Files/        官方 5.2「获取/下发文件」（4 个，扁平）
    ├── Set/          官方 5.1「设置」（3 个，扁平）
    └── Query/        官方 5.1「查询」+ 采样（域 11 个文件 → 分层）
        ├── Interfaces/  IQuery / IDataItemReader / ISampling / ISampleSubscription
        ├── Models/      DataItemRequest / DataItemResult / SampleFrame
        └── Engine/      DataItemReader / DataItemMessages / SampleSubscriptions / SampleMultiplexer
```

**为什么门槛是这两条**：本仓库曾把**每个**域都切成四层，得到 24 个文件夹、
**11 个只装 1 个文件**（`Client/Devices/Interfaces/` 里就 11 行），
命名空间深到 5 段。反过来，域真的大了（`Query` 11 个、`Protocol` 10 个）时，
按作用分层确实好找。

把门槛交给统一判据，**规则会随代码规模自己生效**：库小的时候不许分，
某个域长大了自然放行——不需要每次靠讨论决定。这条由 `check-structure.py`
的 `[2]` 强制（域 `< 8` 却分层、或某个作用夹只有 1 个文件，都硬失败）。

- **接口与实现的名字对应**：`RequestCorrelator : IRequestCorrelator`、
  `DeviceProbe : IDeviceProbe`。分层后接口在 `Interfaces/`，但**名字本身也要能区分**。
- **类型名说明"它是什么"，不用泛化后缀**。`DeviceModelCache` / `RequestCorrelator` /
  `DataItemReader` / `DeviceProbe` 是好名字。
  **本仓库不再有 `-Service` 后缀的实现类**（`NcLinkHostedService` 除外——
  它是 `IHostedService` 的实现，后缀由框架命名约定决定）。
  - 反例一：`ProbeService` 只有一个公开方法、只做一次协议往返 → `DeviceProbe`
  - 反例二：`SamplingService` 做编排没错，**但它本质是一份订阅清单**
    （持有并管理订阅的增删查），"Service"只说它做什么、没说它是什么 → `SampleSubscriptions`
  **判据不是"它是否做编排"，而是"名字能否回答这是什么"。**
- **一个文件夹对应一个功能面**：写入的实现属 `Set/`，不要塞进 `Query/`。
- **命名空间必须与文件夹一致**（`Client/Query/Engine/` → `NcLink.Client.Query.Engine`）。
  移动文件后立刻改命名空间，并删掉因此失效的 `using`。
- **项目根的直接子项不受 `[2]` 约束**（`NcLink.ApiServer/Models/` 这类是 .NET 常规布局）。
- **测试项目不适用可见性规则**（xUnit 只发现 public 测试类）。

---

## 2.5 依赖注入（硬性）

- **构造函数参数一律必填**，不写 `ILogger? = null` 这类可选依赖。
- **生产代码不手工 `new` 组件**：注册到容器，依赖写在构造函数签名上。
- 注册用 `TryAddSingleton<T>()` 让容器解析；**不要手写长串 `GetRequiredService`**。
  仅两个例外：门面 `NcLinkClient`（构造函数是 internal）与 `IOptions<T>.Value`。
- 组件一律**单例**（共享连接与关联表）。
- **`NcLinkClient` 及其组件的构造函数形参是 internal 类型，不要试图改成 public**
  去迁就容器——用 `AddNcLink` 注册。
- **采样转发接线在 `AddNcLink` 的 `RequestPipeline` 工厂里**（`ServiceCollectionExtensions`）：
  `pipeline.ForwardSamplesTo(m => sp.GetRequiredService<SampleMultiplexer>().TryDispatch(m))`。
  **不要把它挪回 `NcLinkClient` 的构造函数**——容器只解析能力接口
  （`IQuery`/`ISet`/`IFileTransfer`）时不会构造门面，接线会被跳过、**采样静默失效**。
  这是真实踩过的坑（`NcLink.ApiServer` 只解析 `IQuery`，采样永远收不到帧）。
  lambda 里必须**延迟解析** `SampleMultiplexer`：它的依赖链会回到 `IRequestPipeline`，
  在工厂内立即解析会形成循环。
- **订阅是"连接生命周期"的职责，只在 `AllResponseFilters` 一处定义。**
  `RequestPipeline.EnsureConnectedAsync` 连接时一次性订阅全部通配符；
  **`RequestCorrelator` 不得按请求再订阅设备专属响应主题**——通配符已覆盖同样的响应，
  两处都订会让同一帧命中两个 MQTT 订阅、broker 各投递一次。
  采样是唯一例外（按通道显式订阅，因此**不要**再给它在 `AllResponseFilters` 里挂通配符）。
  > 这类"两处都订阅"的坑踩过两次：采样（每帧收到两次，实测官方 4 帧 / 我们 8 帧）
  > 与查询响应（带 `@id` 的重复被丢弃属浪费；**不带 `@id` 的重复可能在降级匹配时
  > 被误认领给后续在途请求**，是真隐患）。
- **发布 QoS 默认 0、订阅 QoS 默认 1**，都有实测依据，不要"顺手统一"：
  订阅 1 对齐官方；发布保持 0 是因为 `add`/`delete` **非幂等**，
  请求重传会在设备上执行两次。
- **`-Service` 后缀之外的 DI 扩展约定**：`AddNcLink(...)` 注册协议与客户端，
  `AddNcLinkMqtt(...)` 注册传输——**两个扩展都在 `NcLink` 包内**
  （根目录 `ServiceCollectionExtensions.cs` 与 `Transport/Mqtt/`），
  但保持分开：只用内存传输做测试的调用方不必注册 MQTT。不要逼消费者手工 `new`。
- **不为工厂自造接口**：MQTTnet 5 无 `IMqttClientFactory`，
  用 `Func<IMqttClient>` 工厂委托即可（标准手法）。
- **注册进容器的类型必须同时实现 `IDisposable` 与 `IAsyncDisposable`**
  （`Dispose()` 转调 `DisposeAsync()`）。只实现 `IAsyncDisposable` 会让
  `ServiceProvider.Dispose()` 抛异常，而同步 `using` 是很常见的写法。
- **不能靠 `new` 的东西一律不公开构造函数**：`NcLinkClient` 的构造函数是 internal，
  外部在编译期就无法手工构造（`INcLinkClient` 只能由容器提供）。

## 3. 测试纪律

- **优先写不需要 Broker 的单元测试。** 单测跑完约 1 秒，集成测试约 30 秒。
  判据：**这个行为在 `FakeTransport` 上无法表达吗？**
- **测试对端必须独立拥有自己的认知。** `SimulatedMachine` 刻意自己维护一份
  `FileNodePath`，不复用 `NcLinkProtocolPaths.File`——若复用，客户端路径写错时
  测试会跟着错，就永远验不出问题。**不要"顺手统一"这两处常量。**
- **不要用 `Task.Delay` 轮询等结果**，用 `TaskCompletionSource` + `WaitAsync(timeout)`。
  固定 sleep 在负载高时必然偶发失败（历史上就吃过这个亏）。
- 改了协议匹配逻辑（`RequestCorrelator`）**必须**在 `Correlation/` 相关测试里补用例。
  该组件历史上出过 3 个真 bug，每个都表现为"偶发 + 难以复现"。
- assert 字节数组用 `SequenceEqual`，不用 `Assert.Equal`（后者在 xUnit 里行为不同）。

---

## 4. 已知的实现红线

- **传输实现与使用方之间只隔着 `INcLinkTransport` 接口，这是唯一的解耦点。**
  MQTT 实现（`Transport/Mqtt/`）与核心在**同一个程序集**里——NC-Link 协议
  只有 MQTT 一种传输，不存在"多协议共用抽象、需要可插拔驱动"的场景。
  - **不要为了"解耦"把它拆成独立程序集。** 真正让 180 多个单测脱离 Broker 运行的
    是 `INcLinkTransport` + `FakeTransport`，**不是程序集边界**；
    `NcLink.Simulator` 用核心包却走自己的设备侧收发，也证明了这一点。
  - `MqttTransport` 的 `internal` 成员测试可见，靠核心包的 `InternalsVisibleTo NcLink.Tests`。
  > **审计核实的例外**：类型层面成立（`Client/` 下无一处引用 MQTTnet 或 `MqttTransport`），
  > 但还有一条未声明的第二边——`MqttTransport` 未连接时抛的是 Client 层的
  > `NcLinkNotConnectedException`。按接口自己实现传输的第三方因此必须引用 Client 的
  > 异常命名空间，而接口文档没写"未连接时该抛什么"。要么补文档，要么把该异常下沉到 `Protocol/`。
- **请求-响应关联的两层匹配顺序不可颠倒**，且降级匹配只在响应**未携带** `@id` 时生效。
  改这里之前先读 `RequestCorrelator` 的类注释——写明了为什么。
- **采样主题是 `Sample/{deviceId}/{channelId}`（三层，含设备 SN）**，实测官方设备确认。
  三条后果：
  1. 订阅必须带 SN，且按通道显式订阅（`SampleSubscriptions` 就是这么做的）。
     **不要再在 `AllResponseFilters` 里挂 `Sample/#` 或 `Sample/+`**：
     两处都订阅会让同一帧命中两个 MQTT 订阅、各投递一次 → **每帧收到两次**；
     而 `+` 只匹配一层，也匹配不到三层主题。
  2. 归属是**确定的**，不存在"通道 id 歧义"。**不要重新引入猜测式归因。**
  3. 采样报文的 `data[]` 元素**只有 `data`、没有 `id`**；映射回数据项只能按**位置**
     参照模型里该通道的 `ids` 顺序（`SampleFrame.ItemIds`）。`data` 可为 `null`。
  > 本条曾写反（断言"采样主题不含设备 SN"并据此设计"歧义则丢帧"），
  > 导致采样彻底不可用。**协议细节必须实测，不能推断。**
- **协议常量按类别集中**（`NcLinkTopicPrefixes` / `NcLinkOperationNames` /
  `NcLinkCodeNames` / `NcLinkProtocolPaths`）。**不要建 `Constants.cs` 大杂烩**；
  只被单个组件使用的实现细节留在组件内。
- **不要写 `default(JsonElement)` 当 JSON `null`**，其 `ValueKind` 是 `Undefined`，
  序列化会抛异常；用 `JsonSerializer.SerializeToElement<object?>(null)`。

---

## 5. 操作纪律（都是踩过的坑，违反会损坏仓库）

### 禁止用"文本推算位置"做结构性改动

以下都是真实发生过的事故：

- 移动脚本里"源路径 = 目标路径"的条目，`os.replace(src, src)` **实际是删除该文件**
  → 删掉了 `NcLinkClient.cs`
- 用行号区间提取类型体，区间算错 → 写坏了 `IFileTransfer.cs`，只剩注释没有类型体
- 批量文本替换反复加前缀 → `global using` 变成 `global global global using`
- 头部插入 `using` 的脚本假定文件有 `namespace` → 在顶级语句文件
  （`Program.cs`）和 `global using` 文件里把 `using` 插到了文件中间

**规则**：

1. 写文件操作脚本前，先断言源路径 ≠ 目标路径（用 `os.path.abspath` 比较）。
2. 批量替换前，先确认这个标识符在别处的语义是否相同
   （曾把 `SampleChannelInfo.ItemIds` 和 `RequestContext.ItemIds` 一起改错）。
3. 插入 `using` 前先判断目标文件有没有 `namespace`、是不是 `global using` 文件。
4. **结构复杂时直接重写文件内容，不要靠脚本推算行号。**
5. 破坏性操作（移动/删除/重写）**一次只做一步，做完立刻编译验证**，
   不要攒一批再一起验。

### 检查失败时先问"是代码错还是规则错"

`check-structure.py` 报错时不要为了通过检查而改代码。
曾差点把 `IFileNodeAccessor` 改成 `public` 来迁就一条写错的规则——
正确做法是核实规则本身（列出全部该文件夹内容后确认规则误判）。

### 不要只凭编译通过就报告完成

编译过 ≠ 行为对。改完必须跑 §0 的三条命令。
性能或分配相关的改动要**先测再改、改完再测**（用真实数据，不靠估算）。

---

## 6. 注释与文档

- **`<inheritdoc />` 要写。** 接口已写 `<summary>`，实现类继承说明即可；
  删掉它会让使用者在 IntelliSense 里看不到文档。
- **注释写"为什么"，不写"是什么"。** `// 设置值为 1` 这种删掉；
  要写就写清"为什么两层匹配的顺序不能颠倒"。
- **分区用 `#region` / `#endregion`**，不用 `// ---------- 名称` 注释分隔线。
- **构建文件（`.csproj` / `.props` / `.targets`）里不许有注释。** 说明写进 README / DEVELOPMENT。
  实测 9 处 csproj 注释里，最长的一处几乎是 §4 的逐字复述，
  另有一段在两个 csproj 里逐字重复——写在构建文件里必然与文档重复。
- **行内注释块不超过 4 行。** 注释写"为什么"，一两句就够；
  写到 5 行以上基本是把使用手册或架构说明塞进了注释，那属于 README / 本文档。
  实测 238 个注释块里 227 个（95%）≤3 行，自然断点就在 4 行。
- **日志集中在 `Logging/`**，按组件分文件（`CorrelationLog` 等），
  方法名 `Log<动作>`，EventId 按类分段。**不要在各组件内部写嵌套的 `Log` 类。**
- **死代码直接删**，不要注释掉留着。

---

## 7. 依赖与包

- **只引抽象包**：`Microsoft.Extensions.*.Abstractions`（DI/日志/配置/选项）。
  实现包只出现在测试项目。**唯一例外是 MQTTnet**——它是 NC-Link 协议本身的
  传输依赖（协议就定义在 MQTT + JSON 之上），不是可替换的实现选择。
- **能直接用 BCL 就不要手写**：十六进制用 `Convert.ToHexStringLower` /
  `FromHexString`，GB2312 用 `Encoding.GetEncoding` + `CodePagesEncodingProvider`，
  文件 IO 用 `File.*Async`，定时用 `PeriodicTimer`。
- **只对协议特有的东西自己实现**：请求-响应关联、主题拼接、
  十六进制 + 分块的文件传输、报文形状。这些没有现成库（NC-Link 无 C# 库）。
- **框架自带的功能不手写。** 最典型的教训是 API 文档端点：曾写了 88 行的
  `DocsEndpoints.cs`（手写路由 + 键控服务查找 + 手动序列化），而官方脚手架
  只有 `app.MapOpenApi()` + `app.UseSwaggerUI()` 两行。**先看 `dotnet new webapi`
  生成什么、官方文档怎么写，再动手。**
- **对官方：只保证"用法一致"，不照抄架构。** 这是本仓库最重要的一条边界：

  | | 判据 |
  |---|---|
  | **对外**：URL、HTTP 方法、参数位置、请求体形状、响应形状、错误码 | **必须与官方一致**（否则不能替换）|
  | **对外样式**：Controller + 特性路由，不用 Minimal API | **与官方一致**（厂家的 Java 就是 Controller）|
  | **对内**：类名、文件组织、DI、依赖传递、分层 | **按 .NET 标准，可以且应该优于厂家** |

  **厂家的架构和代码不是优解，不要照搬。** 已犯过的错：

  - 抄了厂家的类名 `DataApi2` → 写成 `Data2Controller`。那个 `2` 是 Java 时代的产物；
    .NET 用命名空间区分版本（`Controllers/V1/DataController`、`Controllers/V2/DataController`）。
  - 抄了 Java 的 `device_id` 参数名。C# 约定是 `deviceId`——**URL 完全相同**
    （路由模板里的占位符名不影响路径），所以这不是对外的，纯属自找的 Java 味。
  - 注释里把"厂家这样写"当成理由。**理由只能是"这是对外用法"或"这是 .NET 标准"**，
    不能是"厂家这样"。
  - 厂家的 `DataApi` 把数据和文件塞在一个类里、方法体直接操作 MQTT 客户端；
    我们分成 `DataController` + `FileController` 的职责边界更清楚。

  反过来，**对外的形状一处都不能少**：官方 `SampleApi` 三个方法都声明了
  GET 与 POST，那就是"用得上的调用方式"，必须照齐（这不是照抄注解）。

- **API 用 Controller 写。** 控制器是 MVC 惯例，也是厂家的形态：
  `Controllers/**` 下按版本放，`[ApiController]` + `[HttpGet]`/`[HttpPost]` 特性路由。

  - **控制器必须 `public`**：MVC 的默认发现机制只认 public 类型（实测 internal 控制器全部 404）。
  - **控制器只做路由与协议编解码**，逻辑放 `Operations/` 的 `internal static` 帮助类
    （`DataOperations` / `FileOperations`）——internal 类型只出现在方法体内
    （不进入 public 签名），因此两边规则都满足，不需要框架 hack。
  - 控制器需要注入的**有状态服务**（如 `ISampleCache`）与实现同放 `Operations/`。
  - **`[ApiController]` 的自动 400 必须关掉**
    （`SuppressModelStateInvalidFilter = true`）：它返回 `ValidationProblemDetails`，
    会破坏官方那套 Spring 形状的 400 错误体。
  - **控制器用的是 MVC 的 JSON 选项**，与 `ConfigureHttpJsonOptions` 是两套，
    必须都配（否则控制器返回的中文会被转义、字段名会被重命名）。
  - **MVC 生成的 OpenAPI 与 Minimal API 不同**，`MvcQuirksOperationTransformer`
    负责收口（catch-all 的 `rest` 参数、multipart 请求体）——只含规则，不含端点清单。
  - **路径参数名不属于契约**：`{device_id}` 与 `{deviceId}` 产出的 URL 相同，
    `check` 因此按归一化后的模板比对（见 `DocsAndSurfaceComparison`）。

- **能从代码/端点推出来的东西不手写**——同一件事的又一次教训：
  手写的 334 行 Swagger 规范（`ApiDocument.cs`）把端点清单**抄了第二遍**，
  端点一改就漂移。现在整条链路只剩 `app.MapOpenApi()` 一行。
  **判据：这段代码里有没有"别处已有的清单"？有就改成推导。**
- **一个库包**：`NcLink`（协议 + 模型 + 客户端 + MQTT 传输 + DI + Hosting）。
  其余项目都是可执行宿主或测试，不作为包发布。

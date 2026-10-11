# NcLink 核心库审计报告

**范围**：`NcLink/NcLink`（57 个 `.cs`，约 4121 行）。
**方法**：五个维度并行独立审计（.NET 标准性 / 冗余精简 / 分层依赖 / 公共 API / 与厂家协议对比），
每条发现要求给出 `文件:行号` + 代码证据 + 可执行提案；**刻意决定必须标注**，不得误报。
**复核**：下面标 ✅ 的条目由我本人回到代码逐条复核过。

---

## 结论速览

### 一、是否标准？——**基本面是标准的，有 4 处真偏差**

批量核对的结论（均为**刻意且有实测依据**，不是问题）：

| 项目 | 结论 |
|---|---|
| async/await | 无 `.Result`/`.Wait()`/`async void`；`ConfigureAwait(false)` 在 61 处调用点一致 |
| 日志 | 5 个日志类**全部**用 `[LoggerMessage]` 源生成器，无字符串插值 |
| DI | `TryAddSingleton`；构造函数参数全必填；生产代码无手工 `new` 组件 |
| 释放 | `IAsyncDisposable` + `Dispose()` 转调，符合 §2.5 |
| 分层 | `Engine/` 无 public 类型、`Interfaces/` 无 class、`Models/` 无 interface（脚本核对通过） |

**真偏差（4 条）**：

1. **四个异常构造函数伪造内部异常**：`base(message, innerException ?? new ArgumentException(message))`。
   而**全部抛出点都没有传 innerException**——这个可选参数与合成逻辑当前是纯成本，
   代价是 `Exception.ToString()` 把同一条消息打印两遍。
2. **`RequestPipeline` 用了可选依赖** `TimeProvider? timeProvider = null` + `?? TimeProvider.System`
   ——这正是 `AGENTS.md §2.5` 明令禁止的写法，且唯一构造点本来就显式传了值。
3. **`SampleFrame` 直接读墙钟** `DateTimeOffset.Now`（全库唯一一处），绕过已注册的 `TimeProvider`
   → 采样时间在无 Broker 的单测里无法断言。
4. **同步 `Dispose()` 桥接无超时上界**：`DisposeAsync` 内 `await _reconnectLoop`，
   而循环体可能正在 `ConnectAsync`（受 `ConnectTimeout` 约束，可配到任意长）；
   且 `NcLinkHostedService.StopAsync` **完全忽略传入的 CancellationToken**，宿主关闭预算形同虚设。

### 二、能否更精简？——**能，约 160 行可直接删除（占 3.9%）**

| 维度 | 可省 |
|---|---|
| 重复骨架、死接口、只写成员、零引用成员 | **约 160 行** |
| 死 `using`（虚构跨层依赖） | 13 行 |
| 消费者不需要的注册扩展 | 21 行 |
| **合计** | **约 194 行** |

同时有 **6 处需要新增约 70 行**补齐厂家协议规则（见第四节）——**净省约 120 行，
但换来的是 6 个静默错误被修掉**。

---

## 一、会静默出错的缺陷（优先修）

### 1. ✅ `ISet.SetAsync` 静默丢弃多索引与区间

`Client/Query/Engine/DataItemMessages.cs:91`

```csharp
var singleIndex = item.Indexes is { Count: 1 } ? item.Indexes[0].Start : (long?)null;
```

- 区间写法 `DataItemRequest.At(path, 0, 9)` 的 `Count` 也是 1 → **只发 Start，丢掉 End**
- 多索引 `Count > 1` → **index 完全不进报文**
- `SetItemAsync` 又把结果压成 `bool`，调用方连"只写了一个"都看不出来

**后果**：用户以为写了 10 个寄存器，实际只写了 1 个，且返回成功。
**修**：显式拒绝（`NcLinkInvalidRequestException`）而不是静默降级。

### 2. ✅ `DeviceModelCache` 取消令牌串台

`Client/Devices/Engine/DeviceModelCache.cs:107-117`

```csharp
var lazy = _inFlight.GetOrAdd(deviceId,
    id => new Lazy<Task<DeviceModel>>(
        () => ProbeCoreAsync(id, timeout, cancellationToken),   // ← 第一个调用方的令牌被共享任务捕获
        LazyThreadSafetyMode.ExecutionAndPublication));
```

并发去重靠 `Lazy`，但工厂只在键未命中时执行一次，于是**第一个调用方的 `timeout` 与
`cancellationToken` 被该设备后续所有并发调用方共用**。A 取消 → B/C/D 一起收到
`OperationCanceledException`，尽管它们自己的令牌从未取消。

**后果**：偶发、与调用方无关的失败。该组件的历史 bug 都是这个形态（`AGENTS.md §3`）。
**修**：共享探测用 `CancellationToken.None`，等待侧各自 `WaitAsync(cancellationToken)`。

### 3. ✅ README 异常表列出 3 个**不存在**的类型

`README.md:146-152`

| README 写的 | 代码里真实的 |
|---|---|
| `DeviceUnavailableException` | `NcLinkDeviceUnavailableException` |
| `ModelPathNotFoundException` | `NcLinkModelPathNotFoundException` |
| `DeviceOperationFailedException` | `NcLinkDeviceRejectedException` |

**后果**：README 是唯一的消费者文档，照它写 `catch` **编译不过**。
且真正最先触发的一类（`NcLinkInvalidRequestException`：模型未加载）**根本不在表里**。

### 4. ✅ README 采样主题表整段过时

`README.md:277` 写 `Sample/{channelId}`（两层），**实际是三层** `Sample/{deviceId}/{channelId`
（`NcLinkTopics.cs:91`）。`README.md:305-310` 还据此描述了一整套"通道 id 歧义则丢帧"机制，
并引用已删除的 `DroppedAmbiguousSampleFrames`——同一份 README 第 76 行的示例却用三层。

**这是"修 bug 前的旧文本"**：`AGENTS.md §4` 记录该写法曾导致采样彻底不可用。
**按这段文档设计系统会直接建出收不到帧的系统。**

### 5. `DownloadToFileAsync` 整文件驻留内存（与同文件刻意做的优化自相矛盾）

`Client/Files/Engine/FileTransfer.cs:175-178`：`var bytes = await DownloadAsync(...); File.WriteAllBytesAsync(...)`

而同文件 113-117 行明确写着：

> 不用 `MemoryStream + ToArray()`：那会先把数据写进扩容缓冲、再整体复制一份，峰值内存翻倍。
> 实测 50 MB 下载由 100 MB 分配降到 50 MB。

**返回 `byte[]` 的路径已按实测优化，落盘的路径没有。**
**修**：抽出分块循环，落盘路径边下边写（峰值从 O(文件) 降到 O(块)）。

---

## 二、与厂家协议对比：我们漏了 6 条规则

这一维度的产出最独特——**厂家代码里有、我们没有的校验**。

### 1. 降级匹配不比对响应 params（厂家逐项比对）

厂商 `QueryResponseItem.match` 要求 `id/operation/length/offset/indexes/keys` **全部相等**；
我们只要求"报文里出现过本次请求的某个数据项 id"。

**后果**：文件分块的典型形态是**同一 id、同一 key、只有 offset/length 不同**——
迟到块会被下一块领走并**写到错误偏移**，文件静默损坏且返回成功。

### 2. `SetParamsMessage` 没有 `length` 字段

厂家上传每块发 `key + offset + length + value`，且要求响应回显 offset/length 才认这一块。
我们只发 `key + offset + value`——而 `FileTransfer.cs:20-22` 的注释却写着
"下发时 value 的十六进制串长度必须是 length 的两倍"。**这条规则在我们代码里表达不出来。**

### 3. 文件下载缺 offset/length 越界校验（**对外契约差异**）

厂家：`length > size || length + offset > size` → **返回 `code 1`**，并写 `Content-Length`。
我们：静默截断，返回 200 + 剩余字节。**违反"对外错误码必须一致"。**

### 4. `sampleInterval` 未按厂家规则回退到 `uploadInterval`

厂家 `ConfigNode.getSampleInterval()` 为空时回退 `uploadInterval`，解析时还会显式赋值。
我们只读 `SampleInterval`。

### 5. `indexes` 与 `keys` 互斥校验缺失

厂家 `QueryParams.isValid()` / `SetParams.isValid()` 要求二者不同时出现，违反直接抛
`InvalidMessageException`（**报文根本不会发出**）。我们会把两者一起写进同一 params。

### 6. 缺路径字符集校验

厂家对 add/delete 与列目录有正则校验（排除空格、反斜杠、全部非 ASCII）。
我们不做任何校验。（注意：厂家对**上传路径**没有该校验，不应误加。）

---

## 三、能删掉的代码（约 194 行）

| 位置 | 行数 | 说明 |
|---|---|---|
| `IRequestCorrelator.cs` | **39** | **零消费者的内部接口**——只被自己的实现类引用，无字段/参数/DI/测试替身。不提供多态、不提供注入点 |
| `RequestContext` 的 3 个只写成员 | **28** | `CorrelationId`（两个构造点都字面传 `string.Empty`）、`ResponseTopic`、`ResponsePayload` 全库无人读取 |
| Query/Set 两个 Engine 抄同一段骨架 | **22** | "解析模型→拼报文→发请求→解码"各抄一遍；`FileTransfer` 已用"私有 QueryAsync/SetAsync 收口"的写法，仓库本身认可这种收口 |
| `NcLinkTopics` 重抄 13 个前缀常量 + 映射写两遍 | **23** | 13 个 `public const` 全是 `NcLinkTopicPrefixes.X` 的同值转发；`ResponseTopicFor`（12 行 switch）与 `ExpectedResponsePrefix`（if 链）是同一张映射表 |
| 零引用成员一批 | **45** | 编码器 `Name` 与非泛型 `Decode`、`NcLinkCodeNames.ToWire`、`PipelineLog.LogConnectionStateChanged`（EventId 2004 无人调用）、`FileTransfer.MaxBlockSize`/`RecommendedBlockSize`、`FileTransferProgress.Fraction`、`ContainsAnyCorrelationId` 的 out 参数 |
| 死 `using` | **13** | 见下 |
| `AddNcLinkCorrelation` + `transportFactory` 参数 | **21** | 生产代码零使用；`services.Configure<CorrelationOptions>()` 就能替代 |

**死 `using` 里最危险的一处**：`Protocol/NcLinkProtocolPaths.cs:1` 反向
`using NcLink.Client.Devices.Models`——**协议层反向依赖 Client**，而
`check-structure.py` 不做 using 分析，编译器也不报。另有 4 处 `using NcLink;`
指向一个没有任何类型声明的命名空间。

**建议**：加 `EnforceCodeStyleInBuild` + `IDE0005 = error`，让构建直接拦住，而不是靠人工。

---

## 四、架构边界：一句话被证伪

`AGENTS.md §4` 说"传输实现与使用方之间**只隔着 `INcLinkTransport`**，这是唯一的解耦点"。

**类型层面成立**（`Client/` 下无一处引用 MQTTnet 或 `MqttTransport`，MQTTnet 只出现在
`Transport/Mqtt/` 的 3 个文件里）——这句是真的。

**但还有第二条边**：`Transport/Mqtt/MqttTransport.cs:163` 反向 `throw` 了
Client 层的 `NcLinkNotConnectedException`。按 §2.5 自己实现 `INcLinkTransport` 的第三方，
要表达"未连接"就必须引用 Client 的异常命名空间——而接口文档对"未连接时抛什么"一字未提。

**还有一处规则自相矛盾**：`AGENTS.md §2` 说 `Hosting/` 是"**唯一**需要
`Microsoft.Extensions.Hosting.Abstractions` 的地方"，但 `Extensions/ServiceCollectionExtensions.cs:146`
的 `AddHostedService<NcLinkHostedService>()` 也在用该包。这条不变量在源码层面不成立。

---

## 五、几处需要澄清的"看似问题"

以下是**刻意决定**，审计确认不是问题，列出以免下次误改：

- 发布 QoS 0（厂家 `SyncClient2.java:42` 也是 0，我们一致）；非幂等操作不能重传
- 主题不带 `clientId`（厂家 v2 的四层主题设备根本不订阅，我们三层才是对的）
- v2 `key.length() <= 3` 魔数校验未采纳（已在 `ReproduceOfficialV2Defects` 下复刻）
- 传输不拆程序集；`Engine/` 只放 internal；接口一律进 `Interfaces/`
- `Protocol/` 报文类型 public（`NcLink.Simulator` 作为设备侧实现要复用）
- `NcLinkHostedService.StartAsync` 的宽 `catch (Exception)`——**是载荷性的**：
  `MqttTransport.ConnectAsync` 不包装异常，收窄会让"EMQX 晚启动不阻断宿主"失效

---

## 六、复核结果（逐条回到代码验证，含被推翻的结论）

审计由五个独立 agent 并行给出，**下面是我逐条复核后的结论**。
把这一节写出来是因为"agent 说有问题"不等于"真有问题"。

### 被推翻的结论（不要照做）

| 原结论 | 复核结果 |
|---|---|
| 降级匹配不比对 params 会导致**文件分块错位写入、文件静默损坏**（列为 high） | **当前不可达**。上传与下载都是顺序发块（`while` 里 `await`，offset 递进），任何时刻只有一个分块请求在途。已把局限与"引入并行分块前必须先补"写进代码注释，**不加代码**——正确修法需在匹配器里解析响应，约 80 行新增，与"更精简"相悖 |
| `transportFactory` 参数与 `AddNcLinkCorrelation` 零使用，可删 21 行 | **错**。`transportFactory` 被 `ContainerLifetimeTests` 等 6 处、`AddNcLinkCorrelation` 被 `ServiceCollectionExtensionsTests` 使用。未删 |
| `NcLinkTopics` 里 13 个常量是 `NcLinkTopicPrefixes` 的重复声明 | **误读**。那 13 个字面量在 `NcLinkTopicPrefixes` 里（规范位置），`NcLinkTopics` 里的是**类内部简写别名**，被本类方法用了约 30 次。删掉只省 13 行却要改 30 处，**不划算，保留** |
| `ISet.SetAsync` 静默丢弃区间与多索引 | **确认，已修**（这是真 bug） |
| `DeviceModelCache` 取消令牌串台 | **确认，已修**，并补了回归测试 |
| README 异常表 3 个类型名不存在 | **确认，已修** |
| README 采样主题写成两层、归因段描述已推翻的机制 | **确认，已修** |
| `IRequestCorrelator` 零消费者 | **确认，已删**（40 行），方法文档搬到实现 |
| `RequestContext` 三个只写成员 | **确认，已删**（编译器确认无人读取） |
| `Protocol/NcLinkProtocolPaths.cs` 反向 `using NcLink.Client.Devices.Models` | **确认，已删**。注意：这个 using 是死的——全文只有一个 `const`，提到 `DeviceModel` 的地方在**注释**里 |
| 异常构造函数伪造内部异常 | **确认，已修**。3 处伪造去掉；`NcLinkProtocolException` 的 `innerException` 参数**有人用**（`SampleMultiplexer`），保留；另两个参数无人用，删掉 |
| `RequestPipeline(TimeProvider? = null)` 可选依赖 | **确认，已改为必填** |
| `SampleFrame` 直接用墙钟 | **确认，已修**。改为由调用方传入 `receivedAt`（模型不该依赖时钟服务），顺带把 `DateTimeOffset.Now` 改成 UTC |
| `SampleChannels` 是每次访问都重建的 LINQ 属性（原列为"线性扫描"） | **确认且比原结论更严重**：采样转发**每帧**都查它，等于每秒重建上千个字典。已改为 `Build` 时建表一次 |

### 审计之外的额外发现

- `NcLinkException.cs` 的 `#endregion` 落在 `NcLinkProtocolException` **类体内部**（错位 2 行）。
  C# 不在乎，读者会在乎。已归位。
- 文件下载 `offset`/`length` **必须成对**：厂家只在两者都给时才按段返回，
  只给一个时两个都忽略。我们此前各自生效——这是审计没列出的对外契约差异，已修。

### 未做（有意）

- 降级匹配补 params 比对（见上，不可达 + 代价不成比例）
- 删 `NcLinkTopics` 的 13 个别名（见上，不划算）
- 删 `AddNcLinkCorrelation` / `transportFactory`（见上，有人用）
- 给 `EnforceCodeStyleInBuild` 配 `IDE0005`：**实测在本 SDK 上不生效**
  （插一个多余的 `using System.Xml;` 探针，构建不报），留着会给人"已拦住"的错觉，故未启用

---

## 待决事项

1. ~~先修哪一批？~~ **已修完**：静默错误（`SetAsync` 丢区间/多索引、`DeviceModelCache`
   取消串台）、对外契约（文件下载越界与 offset/length 成对）、文档不符、精简
   （删 `IRequestCorrelator` 与 `RequestContext` 只写成员、合并 Query/Set 骨架）。
   逐条结果见第六节。
2. **协议规则的设备侧验证**（需要真机，不能推断）：
   - `SetParams.length` 是否是设备**强制**要求（我们已按厂家补上该字段）
   - 路径字符集正则（厂家对 add/delete 与列目录有校验，我们未实现）设备是否强制
3. **精简的破坏性**：第六节里删掉的都是 `internal`（`IRequestCorrelator`、
   `RequestContext` 成员）或经核实无人使用的，`public` 面未做破坏性删减。

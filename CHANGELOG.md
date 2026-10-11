# 变更记录

本项目遵循 [语义化版本](https://semver.org/lang/zh-CN/)。

## [0.1.0] - 2026-10-04

首个版本：用 C# 直接与数控机床通信，跳过官方 Java apiServer。

### 新增

- **NcLink**（核心包）
  - 设备模型：`DeviceModel` / `ModelNode` / `IndexRange`，支持五种路径写法解析到数据项 id
  - 协议：主题规则 `NcLinkTopics`、报文 DTO、`JsonNcLinkCodec` 明文 JSON 编解码
  - 客户端：`NcLinkClient`（探测 / 查询 / 设置 / 采样 / 文件）
  - 传输抽象：`INcLinkTransport` / `INcLinkCodec` / `INcLinkTopics`，应用层不依赖 MQTT
  - 请求-响应关联 `RequestCorrelator`：两层匹配 + 响应主题归属校验
  - 采样分发 `SampleMultiplexer`：通道 id 歧义时丢弃而非错误归因
  - 文件上下行 `FileTransfer`：GB2312 + 十六进制 + 自动分块（50 KiB，上限 100 KiB）
  - **依赖注入**：`services.AddNcLink(...)`，支持代码配置与配置文件绑定，带 `ValidateOnStart`
  - **结构化日志**：基于 `LoggerMessage` 源生成器，覆盖连接、订阅、探测、请求、关联、超时、采样丢弃
  - **异常体系**：`NcLinkException` 分请求侧（超时/路径/参数）与设备侧（不可用/拒绝/未连接/协议）
  - **门面拆分**：`NcLinkClient` 只做装配，职责分到
    `RequestPipeline` / `DeviceProbe` / `DeviceModelCache` / `DataItemReader` /
    `DataItemWriter` / `SampleSubscriptions` / `FileTransfer`；
    `FileTransfer` 依赖窄接口 `IFileNodeAccessor` 而非整个 `INcLinkOperations`
  - **命名空间与文件夹一一对应**（有自动化审计）
- **MQTT 传输**：位于 `NcLink` 包内的 `Transport/Mqtt/`，基于 MQTTnet（含自实现重连与订阅恢复）
- **NcLink.Simulator**：设备侧协议参考实现，可在进程内启动用于离线联调
- **NcLink.Tests**：单元测试（不需要 Broker）+ 真实 MQTT 集成测试
  - 单元测试覆盖：设备模型、协议编解码、请求关联、DI 注册、结构化日志、
    文件传输（分块/偏移/GB2312/十六进制/超长截断/零字节）、
    数据服务（参数构造/失败路径）、采样服务（归因/歧义丢弃）
- **日志集中放置**：所有 `[LoggerMessage]` 声明集中在 `General/Logging/`
  （`CorrelationLog` / `PipelineLog` / `ProbeLog` / `SamplingLog`），
  而不是散在各组件的嵌套类里
- 目录调整：`Device/` 更名 `Model/`（与 `PlcLibrary` 的命名习惯一致）
- 代码整洁：删除 4 处无引用的成员（`NcLinkClientOptions.Clone`、
  `RequestCorrelator.ActiveDevices`、`NcLinkClient.CachedModels`、`DeviceModel.FindById`）；
  装饰性注释分隔线统一改为 `#region`
- **`Client/` 按能力域重排**：每个能力内部统一为
  `Interfaces/`（契约）+ `Models/`（数据形状）+ `Engine/`（实现），三者不混放；
  原来 `Client/Engine/` 一个文件夹里塞了 8 个文件、混了 6 种能力
- 新增 `tools/check-structure.py`：自动审查命名空间一致性与文件夹性质
- 修正能力文件夹内容不纯：`Interfaces/` 只留接口，数据形状移到 `Models/`
  （`RequestContext` / `CorrelationOptions` / `FileEntry` / `FileTransferProgress` /
  `FileTransferLimits`），`ISampleSubscription` 从 `Models/` 移回 `Interfaces/`
- `tools/check-structure.py` 增加「Interfaces/ 只含接口、Models/ 只含数据形状」检查
- 协议常量集中：新增 `NcLinkProtocolPaths`（固定数据项路径），
  与 `NcLinkTopicPrefixes` / `NcLinkOperationNames` / `NcLinkCodeNames` 并列
- 文件传输性能：大文件下载不再用 `MemoryStream` + `ToArray()`（峰值内存翻倍），
  改为按已知总长预分配 + `GC.AllocateUninitializedArray`。
  实测 50 MB 下载由 100 MB 分配 / 18 ms 降到 50 MB / 11 ms；
  下发路径去掉多余的分块复制
- **`Client/` 按官方手册功能面重排**：`Query`（官方 5.1 查询 + 探测 + 采样）、
  `Set`（官方 5.1 设置）、`Files`（官方 5.2 文件）、`Devices`（官方第 8 章数据字典）；
  连接/关联/配置/异常这些**机制**下沉到 `Connection/`，不再与功能域平级
- 接口按能力拆分：`IQuery` / `ISet` / `IFileTransfer`，门面 `INcLinkClient` 组合三者；
  DI 可只注册所需能力
- `NcLink.Model` 命名空间并入 `NcLink.Client.Devices.Models`
- 内部接口从 `Engine/` 移到各自的 `Interfaces/`（`IRequestCorrelator` /
  `IRequestPipeline` / `IDeviceProbe` / `IFileNodeAccessor`），`Engine/` 现在只放实现
- 结构审查规则修正：`Interfaces/` 允许 public 与 internal 接口共存；
  `Engine/` 的可见性规则只适用于生产项目
- 测试文件夹 `Engine/` 更名为 `Client/`（那里是测试类，不是实现）
- 新增 `AGENTS.md`：给 AI Agent 的硬性规则与操作纪律
  （必跑检查、功能边界以官方手册为准、目录三层解剖、
  已知实现红线、以及用文本脚本做结构改动引发的事故记录）
- **配置模型清理**：`NcLinkClientOptions` 删掉 7 个核心包从未读取的 MQTT 字段
  （`BrokerAddress` / `UserName` / `Password` / `ClientId` / `CleanSession` /
  `ConnectTimeout` / `AutoReconnect`）与无人读取的 `SubscribeAllResponseTopics`——
  这些属于 `MqttTransportOptions`，留在核心包会让使用者以为设了就生效
- **超时单一来源**：`CorrelationOptions.DefaultTimeout` 移除，
  请求超时只由 `NcLinkClientOptions.RequestTimeout` 决定，构造时传入关联器
- **设备模型缓存并发去重**：`DeviceModelCache` 增加 `Lazy<Task<T>>` 合并，
  并发请求同一台未缓存设备时只探测一次。实测 8 个并发由 8 次 MQTT 往返降为 1 次；
  探测失败时移除在途记录，避免一次瞬时故障被永久缓存成"该设备总是探测失败"
- `SimulatedMachine` 的后台采样循环接入失败观测：该循环是故意不 await 的，
  异常原会被静默吞掉（模拟器"看起来在跑但不再发采样"），
  现把非取消异常打到 stderr
- 新增 `Client/Devices/Parsers/`：`DeviceModelParser` 从 `Models/` 移出
  （`Models/` 只放数据形状，解析行为单独成文件夹，对齐 `PlcLibrary` 的 `DriverDomain/Parser/`）
- 结构审查新增 `Parsers/` 内容约束
- **拆分 `QueryService`**：它同时实现了查询与设置（名字却只说查询，
  且 `Set/` 文件夹只有接口没有实现）。现拆为
  `Query/Engine/DataItemReader`（官方 5.1 查询）+
  `Set/Engine/DataItemWriter`（官方 5.1 设置），功能面归属随之正确
- 窄接口同步拆分：`IFileNodeAccessor` → `IDataItemReader` + `IDataItemWriter`
- 新增 `NcLinkHostedService` 与 `AddNcLinkHostedService()`：接入 .NET 宿主后
  随宿主启动而连接、随宿主停止而释放。连接失败不阻断宿主启动
  （由传输层重连循环恢复），但取消异常照常向上传递
- **全面改为依赖注入装配（官方推荐方式）**：
  - 去掉 9 处可选依赖（`ILogger? = null` / `TimeSpan? = null` 等），
    全部改为构造函数必填参数——依赖在签名上可见，漏传在编译期暴露
  - 生产代码不再手工 `new` 任何组件；`AddNcLink` 里除门面外全部
    `TryAddSingleton<T>()` 由容器解析构造函数，不再手写 `GetRequiredService` 链
  - `NcLinkClient` 构造函数改为 internal（形参含 internal 类型），
    只应由容器解析；新增 `ISampling`、`IDataItemReader`、`IDataItemWriter` 窄接口
  - `RegisterModel` 从门面移到 `IQuery`
  - 测试基座与集成夹具改为**通过容器构建**，因此每个单测同时验证
    `AddNcLink` 的注册完整可解析
- **MQTT 子库补齐依赖注入支持**（此前消费者必须手工 `new MqttTransport(...)`）：
  - 新增 `AddNcLinkMqtt(...)` / `AddNcLinkMqtt(configuration.GetSection(...))`，
    注册 `INcLinkTransport`、`Func<IMqttClient>` 与 `MqttTransportOptions`（带 `ValidateOnStart`）。
    注册后 `AddNcLink` 不再需要 `transportFactory` 参数
  - `MqttTransport` 注入 **`Func<IMqttClient>`** 工厂委托而非内部 `new MqttClientFactory()`
    （MQTTnet 5 无 `IMqttClientFactory` 接口，故用标准工厂委托，不自造接口），可替换、可测试
  - `MqttTransportOptions` 拆到独立文件并新增 `Validate()`（地址/端口/超时/退避区间）
    与 `SectionName`，风格与核心库的 `NcLinkClientOptions.Validate()` 一致
  - 集成测试改用 `AddNcLinkMqtt`，因此该注册路径被真实 Broker 验证
  - 新增 17 个 MQTT 配置与注册的单测（不需要 Broker）
- `NcLinkHostedService` 从 `Extensions/Hosting/` 上提为顶层 `Hosting/`
  （命名空间 `NcLink.Hosting`）——`Extensions/` 是"怎么注册"的静态助手，
  `Hosting/` 是"被注册的实现"，两者层级不同；单独成域也标出了
  "唯一需要 `Microsoft.Extensions.Hosting.Abstractions`" 这条依赖边界
- **新增 `ContainerLifetimeTests`（7 个测试）**：覆盖容器同步/异步释放、
  幂等释放、注册均为单例等。
- **把编码约定变成可脚本检查的硬约束**（`tools/check-structure.py` 新增 [5] 段）：
  禁止 `// ----` 注释分割线、禁止 `-Service` 泛化后缀、
  公开接口成员必须写 XML 文档、日志类必须集中在 `General/Logging/`、
  `#region`/`#endregion` 缩进一致且必须闭合、不留 TODO/FIXME、
  csproj 必须用 `Microsoft.NET.Sdk`。
  起因：同类违规连续出现多次都靠人工发现——写入文档不构成约束，
  只有让每轮必跑的检查失败才有用
- 新增 `tools/test-check-structure.py`：审查脚本自身的 11 个用例自测
  （含"应放行"的反例，防止把合法写法误报）
- 修掉新检查发现的真实违规：测试目录 11 处 `// ----` 分割线、
  `NcLinkException.cs` 与 `Program.cs` 共 5 处 `#endregion` 缩进不一致、
  测试类名遗留的 `DataServiceTests`
- **为 `NcLink.ApiServer` 补齐 API 文档端点**：官方用 Springfox 2 / Swagger 2.0，
  提供 `/v2/api-docs`、`/swagger-ui.html`、`/swagger-resources`，
  且 `/v3/api-docs`、`/doc.html`、`/actuator/*` 都是 404。这 8 个端点的
  HTTP 状态已逐一对齐。规范里官方文档化的 8 个操作
  （v1/v2 × data/file × GET/POST）**参数、响应码、operationId、summary 逐字段一致**。
  刻意不引 Swashbuckle/NSwag——它们生成 OpenAPI 3.x，而官方契约是 Swagger 2.0。
- **修正文件接口的三处契约偏差**（实测官方行为）：
  1. `path` 与 `key` **都是必填**，缺任一个应返回 400（此前我们只要求 path）；
  2. 响应是 **JSON `DataResponse`** 而非原始字节流，成功时 `value` 为十六进制内容；
  3. POST 上传的表单字段名是 **`value`** 而不是 `file`（用错名字官方表单打不中）。
  另补上 400 的 **Spring 默认错误体**形状（`timestamp`/`status`/`error`/`message`/`path`），
  此前我们返回空体。
- 新增实测错误码：`13`（文件取不到大小，`value` 是消息字符串）、`19`（`get_keys` 传空 key）。
- **移除关联器的冗余响应订阅**：`RequestCorrelator.SendAsync` 每次请求前都会订阅
  设备专属响应主题，而 `RequestPipeline.EnsureConnectedAsync` 在连接时已订阅
  `AllResponseFilters` 的全部通配符、且必然先于它执行 → 同一帧命中两个 MQTT 订阅、
  broker 各投递一次。实测该客户端一度有 9 条订阅（3 条是设备专属），现为 6 条纯通配。
  带 `@id` 的重复响应原本会被丢弃（浪费），**不带 `@id` 的那份要走降级匹配，
  可能在后续请求在途时被误认领**——这是真隐患；同时每次请求还白跑一次 SUBSCRIBE 往返。
- **订阅 QoS 由 0 提升为 1，对齐官方 apiServer**（实测它对设备订阅用 QoS 1）。
  QoS 0 在 broker 侧不重传，报文可能静默丢失、表现为请求超时。
  发布 QoS 仍是 0：`add`/`delete` 非幂等，请求重传会在设备上执行两次，
  宁可超时让调用方决定是否重试。两者都做成了 `MqttTransportOptions` 上的可配置项
  （`SubscriptionQos` / `PublishQos`）。
- **新增 `NcLink.ApiServer`：官方 apiServer 的 C# 可替换实现。**
  路径、请求体、响应体、错误码全部与官方对齐，默认也监听 `19001`，
  调用方改地址即可切换。它只做 HTTP ↔ 库的编解码 + 服务端采样缓存，
  **不含任何协议逻辑**。数据接口经 `NcLink.Conformance --compare` 与官方
  **逐字符比对 15/15 一致**（含 `code` 1/2/3/4/17 全部错误码）。
  已知刻意差异：`GET /model` 返回设备原文（官方是重新序列化，
  多带恒为 null 的 `dataName` 字段），语义等价但非逐字节相同；`/v2/**` 未实现。
- **修正采样重复投递**：`AllResponseFilters` 里的 `Sample/#` 与按通道的显式订阅
  同时命中，MQTT 按匹配订阅各投递一次 → **每帧收到两次**（实测官方 4 帧 / 我们 8 帧）。
  采样改为只按通道显式订阅。
- **修正采样转发接线的位置**：它原本写在 `NcLinkClient` 构造函数里，
  调用方只解析能力接口（`IQuery`/`ISet`）而不构造门面时接线会被跳过、
  **采样静默失效**。已移入 `AddNcLink` 的 `RequestPipeline` 工厂。
  （`NcLink.ApiServer` 正是这样用库的，因此暴露了这个坑。）
- **采样 `beginTime` 按线上原格式输出字符串**：设备报文里它是字符串
  （`"beginTime":"1791349104216"`），库内为 `long?`，此前回写成数字导致形状不符。
- `ModelEndpoints` / 返回码语义等新增实测依据，见 `docs/api-server.md`。
- **多轮一致性验证又找出并修复 8 处真实差异**（前几轮的比较太宽松，只看字段名不看值，
  连续 7 轮扩面 + 固化严格比较后才暴露）：
  1. **`/v2/**` 运行时完全未实现**（文档里有、运行返回 404）。现已实现，且**忠实复刻**
     官方实测行为——官方 0.3.10 的 v2 数据接口恒定超时（20/20 失败、平均 1009 ms，
     日志为 `InteractionManager` 抛 `TimeoutException`），我们返回同样的 `code 16`
     但立即返回；v2 的 model 与 v1 逐字节相同，file 的错误文案少一个 get。
  2. **404/405 响应体是空的**，官方返回 Spring 默认错误 JSON。已用
     `UseStatusCodePages` 统一补上（`timestamp`/`status`/`error`/`message`/`path`）。
  3. **`operation` 空串被当成"缺失"** → 静默成功。官方：缺失才默认 `get_value`，
     显式空串/全空格是非法（`code 2`）。
  4. **`key` 传空串未校验** → 官方返回 `code 19`（2 ms 内，本地参数校验）。
  5. **未知 SN 返回 `code 4`** → 官方返回 `code 19`，且耗时固定 ~3012 ms
     （与请求 `timeout` 无关，是"确保设备已知"的一步）。我们改为先用
     `GetModelAsync` 确保设备模型可用，失败即 19。
  6. **`add` / `delete` 返回 `[null]`** → 官方与 `set_value` 同类，返回**每个 item 一个布尔**
     （`[false]`）。此前只把 `set_value` 当写入路径。
  7. **`GET /model` 缺 `dataName` 且字段顺序不同**。官方把模型解析成 Java POJO 再序列化：
     **`dataName` 不是恒为 null——43 个节点上它等于该节点的 `number`（即变量名）**，
     不补就等于丢内容。新增 `ModelReshaper` 复刻字段顺序与 `dataName`，
     现在与官方**逐字节相同**（11241 字符）；JSON 输出同时关掉非 ASCII 转义。
  8. **文件不存在时我们返回成功空内容**（`{"status":"SUCCESS","value":""}`），
     官方返回 `code 13` + `"get the file size error"`。根因在库：
     `FileTransfer.GetFileEntriesAsync` 把设备返回的 `null` 属性伪造成 **size=0 的条目**，
     `ResolveSizeAsync` 拿到 0、下载出空内容、上层误报成功。
     已修（非对象即"取不到信息"，跳过而不是造假），并补两个回归测试。
     文件错误的外部文案改用官方的**固定字符串**（v1/v2 各一份），真实原因只进日志。
- **立场转变：从"忠实复刻官方"改为"官方正确处对齐，坏处修好"。**
  触发点是反编译了厂家 jar（`nclink-api-server-0.3.10.jar` 内含
  `nclink-core-2.1.3.jar`，142 个类），拿到了权威实现而非实测推断。
  - **v2 默认真正工作**：与 v1 共用同一条正确路径。`POST /v2/{sn}/data/` 合法请求
    从"恒 `code 16`"变成 `code 0` + 真实值；v2 的 `GET`、文件下载同样修好。
  - **v2 错误码精确化**：官方把 `code 1..5` 全塌成 5，我们分别返回 1/2/3/4/5。
  - **新增 `ApiServer.ReproduceOfficialV2Defects`**（默认 `false`）：
    打开后逐项复刻官方缺陷，实测 <b>7/7 与官方一致</b>——
    把"我们不是做不到一致，而是选择做得更好"变成可执行的事实。
    同时还原官方 v2 独有的 `key.length() <= 3` 魔数校验（`code 12`）。
- **错误码全部改用厂家 `StatusCode` 的权威命名**（原先的命名是实测猜的，且猜错了三处）：
  5 是 `REQUEST_ERR_KEY`（不是"v2 的一切参数错误"）、
  13 是 `EXECUTE_ERR_REQUEST`（不是"文件大小错误"）、
  19 是 `EXECUTE_ERR_OTHER`（不是"目标对象不存在"）。
  并修掉一处真实语义错误：**"设备调用失败"此前误用 `code 4`（那是 `REQUEST_ERR_INDEX`）**，
  现改为 `code 14`（`EXECUTE_ERR_EXECUTE`），与厂家实现一致。
- **v2 的根因经反编译确证**（此前只是推断）：`SyncClient2` 把请求发到
  `Query/Request/{SN}/{clientId}`（四层），而设备只订阅三层 `Query/Request/{SN}`；
  叠加 `execute2` 只等 1000 ms（v1 等 4000 ms）。不是"请求级私有应答主题"这么含糊，
  是确定的主题层级错配。
- 反编译同时印证了我们的主题常量与厂家 `Topic.java` **逐条一致**（11 个前缀），
  以及 `Sample/{deviceId}/{channelId}` 的三层结构。
- **文档格式改用原生 OpenAPI 3，删掉 Springfox 兼容层（约 360 行）。**
  文档是给人/工具看的，不是线上契约——要一致的是"怎么调用"。原先为对齐官方
  Swagger 2.0 而声明了 6 个**官方实现根本不读**的查询参数、补了并不返回的
  401/403/404/201、补了并不校验的 `securityDefinitions`——那是把瑕疵当契约，已全部删除。
  比对器同步改为**跨格式归一化后按"怎么调用"比**。
- **修了两处文件接口的真差异**（成功路径此前从未被覆盖，比对用例都打在"文件不存在"上）：
  1. **v1 下载成功时官方回的是原始文件字节流**（Spring 的 `ResponseEntity<Resource>`），
     **且不带 `Content-Type`**，不是 `DataResponse`；我们曾错误地包成
     `{"value":"十六进制"}`。现已照实回字节流。
  2. **v2 的文件下载也是退化的**：文件确实存在时官方恒定 `code 13`
     （`"the file size error"`，约 5030 ms，与 v2 数据接口同源）。已忠实复刻
     （参数校验照旧，其余一律 13，不真等 5 秒）。
- **补齐两个此前未覆盖的场景**（并固化进比对工具）：multipart 上传缺字段的 400 形状、
  v1 文件下载成功路径的原始字节。
- **绑定失败统一收敛成 Spring 错误体**：改非可空形参后 Minimal API 会吐纯文本
  `BadHttpRequestException`，现统一转成 Spring 形状的 JSON；状态码原因短语改用
  BCL 的 `ReasonPhrases`（曾漏掉 415 而落到兜底的 `"Error"`）。
- **`v1 GET data` 是已知的刻意差异**：官方只读请求体而 GET 带不了体，任何 query 都是
  `code 3`；我们读查询参数因此可用。已作为 `[差异]` **显式报出**而不是藏起来。
- **API 文档改为自动生成，删掉 334 行手写规范。** 原先 `Models/ApiDocument.cs`
  把端点清单**抄了第二遍**——端点一改就漂移。现在由内置 `AddOpenApi` 从真实端点
  反射出文档，再用 `Microsoft.OpenApi` 自带的 `SerializeAsV2` 序列化成 Swagger 2.0，
  挂在官方路径 `/v2/api-docs`。Springfox 与 OpenAPI 3 的格式差异集中在三个转换器里
  （`SpringfoxOperationTransformer` / `SpringfoxDocumentTransformer` /
  `SpringfoxJsonNormalizer`），它们**只含规则，不含端点清单**。
  - **不用 Swashbuckle 生成**：它 10.x 移除了 `SerializeAsV2` 选项，只输出 OpenAPI 3.x。
  - **接上标准工具反而暴露了两个真缺陷**，手写规范时看不见：
    ① 处理器返回 `IResult`，生成器遇不到 `DataResponse`，200 响应**引用了一个
    不存在的定义**——改为按官方惯例在端点上声明 `.Produces<DataResponse>(200)`；
    ② **v2 的 GET 处理器带了 `[FromBody]`**（GET 不能有请求体），被生成器的元数据
    推断直接报错——GET/POST 拆成两个处理器。
  - 规范比对仍**8 个操作全部逐字段一致**（含 `operationId`/`summary`/参数表/响应码/
    `DataRequest`、`DataResponse`、`Item` 的定义字段）。
- **一致性验证工具扩到四段**并固化本轮全部用例：新增
  `BehaviorComparison`（v2 端点 / 4xx 错误体 / 路径变体 / operation 大小写 /
  多设备 / 采样边界，共 31 项）。连跑 3 轮全部 15/0 + 31/0 稳定通过。
- **合并 MQTT 传输进核心包，包从 2 个减为 1 个。**
  `NcLink.Transport.Mqtt` 的 3 个文件（433 行）移到 `NcLink/Transport/Mqtt/`，
  MQTTnet 的引用随之移入核心。命名空间 `NcLink.Transport.Mqtt` 不变
  （与文件夹 `Transport/Mqtt/` 一致，仍由结构审查守住）。

  **决策依据**（此前 AGENTS.md 里"必须拆包"的论据经核实不成立）：
  - NC-Link 协议只有 MQTT 一种传输，不存在"多协议共用抽象、需要可插拔驱动"的场景；
  - 真正让 180 多个单测脱离 Broker 运行的是 `INcLinkTransport` + `FakeTransport`
    （42 处使用），**不是程序集边界**——引用 MQTTnet 不需要 Broker；
  - `NcLink.Simulator` 本就自己引用 MQTTnet，并不受益于核心"无 MQTT"；
  - MQTTnet 5.2.0 对 net10.0 **零传递依赖**，238 KB。

  代价只有一个：核心包体积 +238 KB。收益是消费者从两个包变一个包。
  解耦点仍是 `INcLinkTransport` 接口，与程序集划分无关。
- `Directory.Build.props` 与其余构建文件里的注释一并移除；
  检查脚本新增 **[6] 构建文件（csproj/props/targets）不得含注释**，
  且覆盖仓库根（`Directory.Build.props` 不在任何 project 目录下）。
- **修正采样主题（根本性 bug）**：主题是 `Sample/{deviceId}/{channelId}`
  （含设备 SN），此前实现为 `Sample/{channelId}`，导致订阅成功后**永远收不到采样帧**。
  用官方模拟器实测确认（`Sample/{SN}/{ch}` 收 7 帧 / `Sample/{ch}` 收 0 帧）：
  - `INcLinkTopics.Sample(deviceId, channelId)` 签名改为带设备
  - 通配订阅 `Sample/+` 改为 `Sample/#` —— MQTT 的 `+` 只匹配一层，
    匹配不到三层主题（这也是一处独立的订阅缺陷）
  - 移除基于错误前提的"通道 id 歧义则丢帧"机制
    （`DroppedAmbiguousFrames` / `ResolveDeviceByChannel` 及相关日志）——
    SN 在主题里，归属本就确定
  - `SampleFrame` 改为**按位置**索引：报文 `data[]` 元素不带 `id`，
    新增 `Items` / `ValuesAt(index)` / `ItemIds`（来自模型通道配置）/ `ValuesOf(id)`；
    并支持 `data` 为 `null`、值为嵌套对象
  - `SimulatedMachine` 发到正确的主题（此前自建模拟器也错，测试因此全绿却掩盖了 bug）
  - `AGENTS.md` §4 里"采样主题不含设备 SN"这条**写反的规则**已更正
- 目标框架 `net10.0`

### 修复（重构前的实现中发现）

- 探测请求的兜底匹配使用了设备 SN，而设备 SN 也出现在响应主题里，
  导致设备掉线后的**迟到响应被后续请求认领**（表现为"已掉线设备仍返回成功"）。
  现改为两层匹配：严格按 `@id`，降级必须校验响应主题归属。
- `@id` 匹配由裸子串改为 `"@id":"<id>"` 精确形式——关联 id 为短数字时会误命中
  报文里的 `"id":"01035430"`。
- **关联 id 改为全局唯一**（此前是实例级计数器）。实例级计数器会让同一进程内的不同客户端实例
  产生相同的 `@id`，而响应匹配是按文本比对的，于是可能认领到**别的客户端实例**的响应——
  表现为设备在线却偶发报"设备不可用"。该缺陷在集成测试频繁新建客户端时命中率很高，
  现由 `Correlation/CorrelationIdUniquenessTests.cs` 覆盖。
- **严格匹配也校验响应主题归属**。此前只校验关联 id，遇到"关联 id 恰好相同、
  但响应属于另一台设备"的极端情况仍会误领。
- **响应携带了不匹配的 `@id` 时，禁止降级认领**。这是最隐蔽的一处：降级匹配此前只校验
  响应主题与数据项 id，于是设备掉线时的**迟到响应**（属于上一个操作）会被当成本次结果——
  在文件下载上表现为 `FormatException: The input is not a valid hex string`
  （把非文件内容的响应当十六进制解析）。现规则为：响应没带 `@id` 才允许降级；
  带了 `@id` 就说明它属于某个具体请求，不属于本次就绝不认领。
- 复用同一请求对象时沿用旧关联 id，导致两个在途请求串台；现每次发送都指派新 id。

### 已知限制

- 采样主题 `Sample/{channelId}` 不含设备 SN：多台设备共用相同通道 id 时无法归因，
  该帧会被丢弃（可从 `NcLinkClient.DroppedAmbiguousSampleFrames` 观测）。
- 真实机床是否回显 `@id` 尚未现场确认；不回显时非探测请求走降级匹配。
- 官方 apiServer 在测试环境中无法连接设备（`MqttSecurityException: 无权连接`），
  其掉线隔离行为未能验证，详见 `docs/掉线隔离测试报告.md`。

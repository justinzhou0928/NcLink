# NcLink.ApiServer —— 官方 apiServer 的 C# 可替换实现

官方的 `nclink-api-server`（Java / Spring Boot）是设备与上层应用之间的 HTTP 中间件。
本项目用 C# 实现了一个**路径、请求体、响应体、错误码都与之对齐**的等价服务，
因此调用方**改个地址就能切换**。

```
                  ┌──────────────────────────┐
   上层应用 ──HTTP──│ 官方 apiServer (Java)     │──MQTT──┐
                  └──────────────────────────┘        │
                                                       ├── EMQX ── 数控设备
                  ┌──────────────────────────┐        │
   上层应用 ──HTTP──│ NcLink.ApiServer (C#)    │──MQTT──┘
                  └──────────────────────────┘
```

## 它做什么、不做什么

| | |
|---|---|
| **做** | HTTP 形状 ↔ 库调用的编解码；服务端特有的采样缓存 |
| **不做** | 任何协议逻辑（主题拼接、报文形状、请求-响应关联）——那些全在 `NcLink` 库里 |

`NcLink.ApiServer` 是本仓库里**唯一**用 `Microsoft.NET.Sdk.Web` 的项目
（其余一律 `Microsoft.NET.Sdk`，由 `tools/check-structure.py` 强制）。

## 端点

| 方法 | 路径 | 说明 |
|---|---|---|
| GET / POST | `/v1/{device_id}/data/**` | 数据查询 / 设置 |
| GET | `/v1/{device_id}/file/**` | 取文件（`?path=&offset=&length=`）|
| POST | `/v1/{device_id}/file/**` | 下发文件（multipart + `path`）|
| GET | `/v1/{device_id}/model` | 取设备模型（返回设备原文）|
| POST | `/v1/{device_id}/model` | 注入模型，跳过探测 |
| POST | `/v1/sample/{device_id}/sub/{channel_id}` | 订阅采样，返回 `sub_id` |
| GET | `/v1/sample/{device_id}/data/{sub_id}` | 取走缓存帧（drain）|
| POST | `/v1/sample/{device_id}/unsub/{sub_id}` | 取消订阅 |

## API 文档端点

官方 apiServer 用 **Springfox 2 / Swagger 2.0**。实测它提供与不提供的端点：

| 端点 | 官方 | 我们 |
|---|---|---|
| `GET /v2/api-docs` | HTTP 200（Springfox 的 Swagger 2.0）| **HTTP 404**（我们改用框架原生路径，见下）|
| `GET /swagger-ui.html` | HTTP 200 | HTTP 200 |
| `GET /swagger-resources` | HTTP 200 | HTTP 200 |
| `GET /` | HTTP 200 | HTTP 200 |
| `GET /v3/api-docs` | HTTP 404 | HTTP 404 |
| `GET /doc.html` | HTTP 404 | HTTP 404 |
| `GET /actuator`、`/actuator/health` | HTTP 404 | HTTP 404 |

**规范由内置生成器自动产出，不手写**：`AddOpenApi` 从**真实端点**反射出文档，
再由 `Microsoft.OpenApi` 自带的 `SerializeAsV2` 序列化成 Swagger 2.0，
挂在官方路径 `/v2/api-docs`。**端点清单一处都不抄**——新增端点自动进文档。

> 这里曾走过弯路：一开始**手写了 334 行的规范**（`Models/ApiDocument.cs`）。
> 它逐字段对得上，但它把端点清单**抄了第二遍**，端点一改就漂移。
> 正确的分层是——**能自动推的绝不手写，推不出来的集中在转换器**。

**为什么不用 Swashbuckle 生成**：Swashbuckle 10 移除了 `SerializeAsV2` 选项
（它在 OpenAPI.NET 2.x 迁移时被删掉），只输出 OpenAPI 3.x；
而底层 `Microsoft.OpenApi` 的 V2 序列化仍然可用。
所以是"**内置生成器 + 库自带序列化**"，而不是"生成器 + 自写转换器"。

三处 Springfox 特有、OpenAPI 3 **表达不出来**的东西放在
`SpringfoxJsonNormalizer`（序列化后的 JSON 层）：

| 差异 | 为什么对象模型里做不到 |
|---|---|
| `formData` 参数 | **OpenAPI 3 没有这个位置**——multipart 上传是 `requestBody` 的 content |
| 请求体参数名 `dataRequest` | OpenAPI 3 的 `requestBody` **没有名字**，V2 序列化统一写 `body` |
| `basePath: "/"` | V2 序列化只从 `Servers` 推出 `host`，不输出 `basePath` |

另有 `SpringfoxOperationTransformer`（按路由推 `operationId`/`summary`/参数表）
与 `SpringfoxDocumentTransformer`（tags / securityDefinitions / 排除运维端点）。

**格式用原生 OpenAPI 3，不刻意对齐官方的 Swagger 2.0。** 文档是给人/工具看的，
不是线上契约——要一致的是"怎么调用"，那由行为面比对保证。为格式一致养一套
Springfox 兼容层（约 360 行）等于把官方文档里的瑕疵也一起复刻，已删除：

| 曾为对齐而做的事 | 为什么删掉 |
|---|---|
| 声明 data 端点的 6 个查询参数 | **官方实现根本不读它们**（query 与 body 冲突时以 body 为准），声明不生效的参数是误导 |
| 统一补 401/403/404/201 响应 | 我们并不返回它们，照抄是假声明 |
| 补 `securityDefinitions` 的 Authorization | 官方声明了但不校验，我们也不校验，声明不生效的授权同样误导 |

**文档路径也改为框架原生**——官方脚手架就是 <c>app.MapOpenApi()</c> 一行：

| | 官方 | 我们 |
|---|---|---|
| 规范 | `GET /v2/api-docs`（Springfox）| **`GET /openapi/v1.json`**（`MapOpenApi()`）|
| 界面 | `GET /swagger-ui.html` | **`GET /swagger`**（`UseSwaggerUI` 默认前缀）|
| `GET /swagger-resources` | 200 | **404**（Springfox 独有，Swagger UI 3 不用它）|

对应的 `Endpoints/DocsEndpoints.cs`（88 行手写路由 + 键控服务查找 + 手动序列化）
**已整个删除**——那些都是框架自带的。

### 规范比对结果

官方文档化 **8 个操作**（v1/v2 × data/file × GET/POST），逐字段比对：

```
[一致] GET  /v1/{device_id}/data/**      [一致] POST /v1/{device_id}/data/**
[一致] GET  /v1/{device_id}/file/**      [一致] POST /v1/{device_id}/file/**
[一致] GET  /v2/{device_id}/data/**      [一致] POST /v2/{device_id}/data/**
[一致] GET  /v2/{device_id}/file/**      [一致] POST /v2/{device_id}/file/**
[额外] GET/POST /v1/{device_id}/model（官方实现了但未文档化）
[一致] definition DataRequest / DataResponse / Item(DataRequestItem) 的字段
[不比对] 文档格式：官方=Swagger 2.0 我们=OpenAPI 3（只比使用方式）
```

比对方式已改为**跨格式归一化后按"怎么调用"比**：Swagger 2 的 `body`/`formData` 与
OpenAPI 3 的 `requestBody` 归一成同一视图，只比名字、位置、类型、必填。
**查询/路径参数不作为失败项**（官方那 6 个声明了不生效），只有请求体与表单字段
才算契约。

> **`DataResponse` 的 schema 是自动生成暴露出来的一个真缺陷**：处理器返回
> `IResult`，生成器遇不到 `DataResponse` 这个类型，于是 200 响应**引用了一个
> 不存在的定义**。修法是按官方惯例在端点上声明 `.Produces<DataResponse>(200)`——
> 这既让 schema 生成出来，也在代码里写明了响应类型。同类问题还有：
> **GET 处理器带 `[FromBody]`**（GET 不能有请求体）被生成器的元数据推断报错，
> 因此 v2 的 GET/POST 拆成了两个处理器。
> **这两处都是"接上标准工具后自己冒出来的"**，手写规范时反而看不见。

### 两处刻意差异

1. **我们的规范是官方的超集**：额外文档化了 `model`（2 个操作）。
   官方的 `ModelApi`/`SampleApi` 没有 Springfox 注解，**文档比能力少**——
   我们如实文档化自己实现的端点，官方文档里的每个操作仍逐字段一致。

2. **不含 6 个 Java 专有 definition**：`File` / `InputStream` / `Resource` /
   `URI` / `URL` / `URLStreamHandler`。它们是 Springfox 扫 `MultipartFile`
   的 Java 继承链带出来的，构成一个**与任何操作都不连通的孤岛**
   （`Resource` 被引用 0 次，其余只在岛内互相引用），不属于 HTTP 契约。
   注意 `Item` 不同——它经 `DataRequest.properties` 可达，因此必须保留。

## 数据接口契约

请求体（与官方 `DataRequest` 一致）：

```json
{ "operation": "get_value", "items": [ ... ], "timeout": 8000 }
```

`items[]` 的字段：

| 字段 | 类型 | 实测语义 |
|---|---|---|
| `path` | string | 数据项路径，如 `/MACHINE/CONTROLLER/VARIABLE@REG_X` |
| `index` | **int 或 int[]** | `0` → 1 个值；`[0,1,2]` → 3 个值。**区间字符串 `"0-7"` 被拒绝** |
| `key` | string 或 string[] | HASH 型数据项用 |
| `offset` | int | 对数据项只在 `set_value` 时表示位偏移 |
| `length` | int | **对数据项不生效**（实测传 `offset`+`length` 返回 `[null]`）|
| `value` | any | 写入值 |

响应体（与官方 `DataResponse` 一致）：

```json
{ "status": "SUCCESS", "code": 0, "value": ... }
```

`value` 的形状**按操作分流**（实测）：

| 操作 | `value` | 例 |
|---|---|---|
| 查询类 `get_*` | 外层数组，**每个 item 一个值数组**，无值时该元素为 `null` | `[[123,0,0]]`、`[[512]]`、`[null]` |
| 设置类 `set_value` | 外层数组，**每个 item 一个布尔** | `[true]` |
| 多个 items | 外层每个 item 一元素 | `[[123],[0]]` |

### 错误码（全部实测，非推断）

| 场景 | `status` | `code` |
|---|---|---|
| 成功 | `SUCCESS` | `0` |
| `items` 缺失 | `FAILED` | `1` |
| `operation` 非法（含**空串、全空格**）| `FAILED` | `2` |
| `path` 缺失 / 空串 / 模型里解析不到 | `FAILED` | `3` |
| 参数形状非法（`index` 传字符串、数组含非整数）/ 设备调用失败 | `FAILED` | `4` |
| **v2 数据接口的一切参数错误** | `FAILED` | `5` |
| 文件接口取不到文件大小 | `FAILED` | `13`（`value` 是消息字符串）|
| **v2 数据接口的合法请求** | `FAILED` | `16`（恒定超时，见下）|
| 采样子 `sub_id` 不存在 | `FAILED` | `17` |
| **目标对象不存在**：未知 SN / `key` 传空串 | `FAILED` | `19` |
| `operation` **缺失**（不是空串）| `SUCCESS` | `0`（默认 `get_value`）|
| `items: []` | `SUCCESS` | `0`，`value: []` |

失败时仍返回 **HTTP 200**，靠 `status` + `code` 表达——与官方一致。

三处只看表看不出、必须实测才分得清的语义：

**① `operation` 缺失 ≠ 空串。** 缺失（字段不存在）默认 `get_value` 并成功；
显式给 `""` 或 `" "` 是**非法**（`code 2`）。曾把两者都当"缺失"，于是空串静默成功。

**② `code 19` 有两个来源，靠时延区分：**

| 来源 | 时延 | 机制 |
|---|---|---|
| `key` 传空串 | **2 ms** | 本地参数校验 |
| 未知 SN | **约 3012 ms** | 固定 3 秒探测窗口 |

未知 SN 的耗时**与请求里的 `timeout` 无关**（设 1000 或 20000 都是 3012 ms），
说明它是"先确保设备已知"的那一步失败，而不是请求超时。官方日志给出根因：

```
java.util.concurrent.TimeoutException: null
    at ...apiserver.core.InteractionManager
```

我们按同样语义实现：先 `GetModelAsync` 确保设备模型可用，失败即 19。

**③ `set_value` / `add` / `delete` 三者都返回"每个 item 一个布尔"**，不是值数组：

```
set_value → {"value":[true]}      设备接受
add       → {"value":[false]}     模拟器不支持该操作，设备回绝
delete    → {"value":[false,false]}   两个 items
```

曾只把 `set_value` 当写入，`add`/`delete` 落到查询路径返回 `[null]`。

## 文件接口

`GET /v1/{device_id}/file/**`，`POST` 同路径（multipart 上传）。

**实测要点**：

| 要点 | 说明 |
|---|---|
| `path` 与 `key` **都是必填** | 缺任一个 → **HTTP 400**。`path` 是 FILE 数据项路径，`key` 是文件名 |
| 响应是 **JSON `DataResponse`** | 不是原始字节流。成功时 `value` 是十六进制内容 |
| 400 的错误体是 **Spring 默认形状** | `{"timestamp","status","error","message","path"}`，与数据接口的 `DataResponse` 是两套 |
| POST 的表单字段名 | `path` / `key` / **`value`**（承载文件）。上传字段叫 `value`，**不是 `file`** |
| 文件不存在的返回码 | `{"status":"FAILED","code":13,"value":"get the file size error"}` |

> **成功形状未能在本环境实测**：官方模拟器的 G 代码目录是空的
> （`get_keys` 返回 `[null]`，任何 `key` 都报 size error）。这里按协议约定
> 返回十六进制字符串。错误路径的形状与返回码已逐字符验证一致。

## v2：官方是坏的，我们修好了

**立场：官方正确处对齐，坏处修好。** 官方 0.3.10 的 v2 是系统性坏掉的，
根因在反编译 `nclink-core-2.1.3.jar` 后已<b>确证</b>，不再是推测。

### 根因（来自反编译，不是实测推断）

`SyncClient2` 与 `SyncClient`（v1）的唯一差别是<b>主题多了 `clientId`</b>：

```java
// SyncClient（v1）—— 设备订阅的就是这个
Topic.queryRequest(deviceId)                // Query/Request/{SN}

// SyncClient2（v2）—— 设备根本没订阅
Topic.queryRequest(deviceId, this.clientId) // Query/Request/{SN}/{clientId}
```

`apiServer` 把请求发到<b>四层主题</b>，而设备只订阅三层，
于是<b>请求发到了没人收的主题上</b>，永远等不到响应。

叠加第二层因素——`InteractionManager.execute2` 只等 **1000 ms**（v1 的 `execute` 等 4000 ms），
所以失败来得更快。可观测量就是「v2 数据接口恒定 `code 16`，约 1009 ms」。

第三处：参数错误被 `case 1..5:` **全部塌成 `code 5`**，丢失了具体原因
（`code 5` 的真名是 `REQUEST_ERR_KEY`）。

### 我们的默认行为 vs 官方

| 用例 | 官方 | 我们（默认）|
|---|---|---|
| `POST /v2/{sn}/data/` 合法请求 | `code 16`（失败）| **`code 0` + 真实值** |
| 缺 `items` | `code 5` | **`code 1`** |
| `operation` 非法 | `code 5` | **`code 2`** |
| `path` 缺失 | `code 5` | **`code 3`** |
| `index` 字符串 | `code 5` | **`code 4`** |
| `GET /v2/{sn}/data/?path=..` | `code 5`（不可用）| **`code 0` + 真实值** |
| `GET /v2/{sn}/file/?path=..&key=..` | `code 13`（失败）| **文件原始字节流** |
| `items: []` | `code 0` + `[]` | 同（本来就对）|

实现上 v2 与 v1 **共用同一条正确路径**——两条路由的参数与响应形状本来就相同，
官方把它们分成两套（且 v2 那套是坏的），我们只保留一份。

### 复刻模式：`ApiServer.ReproduceOfficialV2Defects`

默认 `false`。<b>打开后逐项复刻官方的缺陷</b>，实测 7/7 与官方一致：

```powershell
dotnet run --project NcLink.ApiServer -- --ApiServer:Port=19003 --ApiServer:ReproduceOfficialV2Defects=true
```

| 用例 | 官方 | 复刻模式 |
|---|---|---|
| 合法请求 | 16 | 16 |
| 缺 items / operation 非法 / path 缺失 / index 字符串 | 5 | 5 |
| GET 带 query | 5 | 5 |
| items 空数组 | 0 | 0 |

用途是<b>向厂家证明"我们完整理解你的实现"</b>，而不是日常运行。
它同时把"我们不是做不到一致，而是选择做得更好"变成可执行的事实。

> 复刻模式还还原了官方 v2 独有的一条魔数校验：
> `key.length() <= 3` 直接拒（`code 12` + `"params error"`，v1 没有这条）。
> 它没有依据，只在复刻模式下存在。

### 官方 v2 的其它已知问题（我们不复刻）

- `DataApi2.java:70` 把 `"/v1".length()` 硬编码去切 `/v2/` 的 URI——
  因为两者都是 3 字符，<b>凑巧没错</b>，是潜伏 bug。
- `request.timeout` 被夹到 `[10, 30000]` 后<b>根本没用于等待</b>，
  `future.get()` 是硬编码的 4000/1000 ms。请求体里那个 `timeout` 字段形同虚设。

## 模型端点：复刻官方的重新序列化

官方把模型解析成 Java POJO 再序列化，因此输出与设备原文有两处差异。
**`ModelReshaper` 复刻了这个形状，现在与官方逐字节相同（11241 字符）。**

**① `dataName` —— 这不是格式差异，是内容。**

设备原文里**没有** `dataName` 字段。官方给**数据节点**填的是该节点的 `number`：

| `dataName` 取值 | 节点数 |
|---|---|
| 非 null（等于该节点的 `number`，如 `REG_X` / `EVENT` / `PROGID_MAP`）| **43** |
| `null` | 59 |

43 正好等于模型里 `VARIABLE` 节点的数量。**不补 `dataName` 就等于把变量名丢了。**

> 排查时曾一度断言"`dataName` 恒为 null"——那是因为只看了前 22 个节点，
> 而它们恰好都是 `POSITION`/`SPEED` 这类没有 `number` 的类型。**样本偏差。**

**② 字段顺序**跟着 Java 类声明走。用拓扑排序从全部节点推出偏序：

```
数据节点（102 个）: dataName → name → id → type → settable → number → dataType
                    → value → sampleInterval → ids → uploadInterval
结构节点（21 个）:  name → id → type → description → devices → number
                    → configs → dataItems → components → version
```

判据是**带子节点（`devices`/`configs`/`dataItems`/`components`）的是结构节点**，
用结构顺序且不加 `dataName`；其余是数据节点。

**③ 中文不转义。** 官方 JSON 里是 `"name":"机床模型文件"` 而不是 `\uXXXX`，
因此 ApiServer 的 JSON 选项关了非 ASCII 转义（`UnsafeRelaxedJsonEscaping`）。

### v2 端点

官方同时暴露 `/v2/**`。反编译显示 `DataApi2` 与 `DataApi` 的对外形状**完全一致**
（同样的 URL、方法、参数、响应），差别只在实现里。

**本项目已实现 v2**，而且**默认复用 v1 的正确实现**——对外形状相同就不该有两份逻辑。
官方的 v2 是坏的（根因见下文「v2 为什么是坏的」），我们把它修好了。

`ApiServerOptions.ReproduceOfficialV2Defects` 打开时改为逐项复刻官方缺陷，
用于向厂家证明"我们完整理解你的实现"。默认关闭。

> 这一节此前写的是"暂未实现 v2，先确认真有调用方需要"——那是早期状态，已过时。
> v2 现在有完整实现，并由 `NcLink.Conformance` 逐条比对官方（6/6 达成）。

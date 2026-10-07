# 官方模拟器实测结果（2026-07-07）

对官方设备模拟器 `StressTest-INC-Cloud.exe`（10 台 `STRESS_TEST_00000`~`00009`）
经 EMQX 5.3.0 实测得到。**这是 MQTT 层契约的唯一权威来源**——
官方手册只写 HTTP 层，apiServer JAR 里没有报文定义。

## 测试条件

- EMQX：`<EMQX_HOME>`，监听 127.0.0.1:1883
- 模拟器：`../厂家资料/NC-Link设备模拟器\StressTest-INC-Cloud.exe`
  （`config.yaml` 的 `Mqtt.IP` 已改为 127.0.0.1）
- 设备每个订阅 6 个主题（由 `emqx_ctl topics list` 确认）：
  `Query/Request/{SN}`、`Set/Request/{SN}`、`Probe/Query/Request/{SN}`、
  `Probe/Set/Request/{SN}`、**`Probe/Version/Response/{SN}`**、**`Register/Response/{SN}`**

## 报文契约（实测）

### 探测 Probe

请求 `Probe/Query/Request/{SN}`，报文体 `{}` 或 `{"@id":"..."}` 均可。

响应 `Probe/Query/Response/{SN}`：

```json
{ "code": "ok", "probe": { "id": "01", "type": "NC_LINK_ROOT", "name": "机床模型文件", "devices": [ ... ] } }
```

**要点**：
1. **模型嵌在 `probe` 字段里**（不是顶层 `model`）
2. **响应不回显 `@id`** —— 探测只能靠主题归属 + `probe` 结构识别
3. `code` 是小写 `"ok"`（与 Query/Set 响应里的大写 `"OK"` **不同**）
4. `probe` 的结构与 `model.json` 完全一致（`devices` → `components` → `dataItems`/`configs`）

### 查询 Query

请求 `Query/Request/{SN}`：

```json
{ "@id": "a3", "ids": [ { "id": "01035430", "params": { "operation": "get_value", "indexes": ["0-7"] } } ] }
```

响应 `Query/Response/{SN}`：

```json
{ "values": [ { "id": "01035430", "code": "OK", "params": { "operation": "get_value", "indexes": ["0-7"] }, "values": [0,0,0,0,0,0,0,0] } ], "@id": "a3" }
```

实测的各 operation 行为（数据项 `01035430` = `REG_X`，`dataType=LIST`，`settable=true`）：

| 请求 | 结果 |
|---|---|
| 无 `params` | `code:"NG"` |
| `params.operation=get_value` 无区间 | `code:"NG"` |
| `params.operation=get_value` + `indexes:["0-7"]` | **`code:"OK"`，8 个值** |
| `params.indexes:["0-7"]`（不带 operation） | `code:"OK"`，8 个值 |
| `params.operation=get_length` | `code:"OK"`，`values:[512]` |
| `params.operation=get_keys` | `code:"NG"`（该数据项不是 HASH） |
| `params.operation=get_attributes` | `code:"NG"` |
| `offset`+`length` 形式 | `code:"NG"` —— **该数据项不认这种取法** |
| 用 `path` 代替 `id` | **设备无响应** —— 只认模型 `id` |

### 设置 Set

请求 `Set/Request/{SN}`：

```json
{ "@id": "b2", "values": [ { "id": "01035430", "params": { "operation": "set_value", "index": 0, "value": 42 } } ] }
```

响应 `Set/Response/{SN}`：

```json
{ "results": [ { "id": "01035430", "code": "OK", "params": { "operation": "set_value", "index": 0 } } ], "@id": "b2" }
```

| 请求 | 结果 |
|---|---|
| `set_value` **不带** `index` | `code:"NG"` |
| `set_value` **带** `index:0` | **`code:"OK"`** |
| `add` + `key` | `code:"NG"` |

### 模型节点结构

```
NC_LINK_ROOT (id=01)
└── MACHINE (id=0103, version=1.2)
    ├── components/dataItems ...
    │   └── VARIABLE (id=01035430, number=REG_X, dataType=LIST, settable=true)   ← 无 length 字段
    └── configs
        └── SAMPLE_CHANNEL (id=sample_channel0, sampleInterval=3000, ids=[{id:010302},...])
```

- 43 个 `VARIABLE`、3 个 `SAMPLE_CHANNEL`（`sample_channel0` / `event_upload` / `sample_channel1`）
- `VARIABLE` 的键：`id` / `type` / `number` / `name` / `dataType` / `settable`
  （`LIST` 数据项**没有** `length` 字段 → 长度要靠 `get_length` 问）

### 尚未验证

- `Probe/Version`：设备订阅的是 `Probe/Version/Response/{SN}`。
  试过请求主题 `Probe/Version/Request/{SN}` 与 `Probe/Version/{SN}`，**均无响应**。
  正确请求主题待查（可能由平台侧下发，或主题名不同）。
- `Register/Response/{SN}`：设备订阅了它，但**未观测到任何报文**。
  推测设备上线时先发 `Register/Request`、平台回 `Register/Response/{SN}`，
  而本次测试的观测客户端订阅得太晚（设备早已完成注册）。

## 文件访问（实测）

官方模型的 `FILE` 节点：`id=01035408`、`type=FILE`、`name=G代码文件`、**`number` 为空**。

| 请求 | 响应 |
|---|---|
| `get_keys` | **`code:"OK"`，但响应里没有 `values` 字段** |
| `get_keys` + `key:"/"` | 同上（`code:"OK"`，无 `values`） |
| `get_attributes` | `code:"NG"` |
| `get_length` | `code:"NG"` |
| `get_value` + `key:"O0001"` | `code:"NG"` |
| 无 `params` | `code:"NG"` |

**要点**：`get_keys` 成功时**省略 `values` 字段**（而不是给 `values: []`）。
客户端的 `ListFilesAsync` 若把"没有 `values`"当作空列表，就永远取不到文件——
需按 `code` 判断成功、`values` 缺失时按协议约定处理。

## 采样（实测）

订阅 `Sample/sample_channel0`、`Sample/event_upload`、`Sample/sample_channel1`
共 15 秒，**收到 0 帧**。

推测原因：官方模拟器的 `configs` 里虽有 `sampleInterval` / `uploadInterval`，
但采样可能还需要平台侧先下发使能/配置（`Probe/Set` 或 `Set`）才会启动。
**此项未验证**——需要官方模拟器使用说明之外的信息，或对照 apiServer 的实际调度逻辑。

## 本次实测发现并修复的实现缺陷

| 缺陷 | 后果 | 修复 |
|---|---|---|
| 探测响应找顶层 `model` 字段，实际是 `probe` 对象 | **探测永远匹配不上 → 所有依赖模型的操作全部超时** | `ProbeQueryResponseMessage.Probe` 改为 `JsonElement`；`DeviceProbe` 匹配 `probe` |
| 未订阅 `Register/Response/{SN}`、`Probe/Version/Response/{SN}` | 这两类报文收不到 | `AllResponseFilters` 补上（8 条） |
| `BrokerFixture` 传入自定义 `ILoggerFactory` 时未绑定开放泛型 `ILogger<>` | 组件解析失败 | 补 `AddSingleton(typeof(ILogger<>), typeof(Logger<>))` |

**仍存在、待修的缺陷**（已确认，未改）：

| 缺陷 | 证据 |
|---|---|
| `ListFilesAsync` 依赖 `values` 数组，但 `get_keys` 成功时**省略该字段** | 对官方模拟器实测返回 0 个文件 |
| 采样订阅后收不到帧 | 15 秒 0 帧（原因待查） |

## 采样（实测，2026-07-07 补测）

### 主题形状

```
Sample/{deviceId}/{channelId}     ← 含设备 SN
```

用两个候选主题并行订阅 20 秒的结果：

| 订阅 | 收到帧数 |
|---|---|
| `Sample/STRESS_TEST_00000/sample_channel0` | **7 帧** ✅ |
| `Sample/sample_channel0` | **0 帧** ❌ |

**通配订阅必须用 `Sample/#`**：MQTT 的 `+` 只匹配一层，
`Sample/+` 匹配不到三层的 `Sample/{SN}/{ch}`。
（用 `emqx_ctl topics list` 也能看到实际被订阅的主题带 SN。）

### 帧结构（完整抓包，201 字符）

```json
{"id":"sample_channel0","beginTime":"1791349104216",
 "data":[{"data":[1]},{"data":[0]},{"data":[100]},{"data":[0]},
         {"data":[[{"number":"01020340","text":"PLC报警"},
                   {"number":"01040340","text":"伺服报警"}]]}]}
```

**三个要点**：

1. `data` 可以是 **`null`**（`event_upload` 通道实测如此）；
2. `data[]` 的每个元素**只有 `data`、没有 `id`** —— 报文不携带数据项 id，
   映射回数据项只能按**位置**参照模型里该通道的 `ids` 顺序；
   实测 `sample_channel0` 模型声明 6 个 id、帧里 5 项，
   **个数可能不等**，所以 `SampleFrame` 只提供位置索引 +
   与 `ItemIds` 对齐时的 `ValuesOf(id)` 便捷方法；
3. 值可以是嵌套数组/对象（报警项形如 `[[{number,text}]]`），不做强制解包。

### apiServer 的采样访问方式

| 步骤 | 接口 |
|---|---|
| 订阅 | `POST /v1/sample/{sn}/sub/{channelId}` → 返回 `sub_id`（GUID） |
| 取数 | `GET /v1/sample/{sn}/data/{sub_id}` → 服务端缓存的帧 |
| 取消 | `POST /v1/sample/{sn}/unsub/{sub_id}` |

apiServer 是**服务端缓存 + 轮询**；本项目是**事件推送**。
两者能力等价、访问风格不同，已用一致性验证确认同一通道两边都能取到数据。

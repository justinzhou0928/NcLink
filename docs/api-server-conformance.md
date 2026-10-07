# 与官方 apiServer 的一致性验证

用<b>两条官方路径</b>对<b>同一台官方模拟设备</b>取同一份数据，逐项比对结果。
两条路径最终都与同一台设备对话，因此语义结果必须一致。

| 路径 | 客户端 | 契约来源 |
|---|---|---|
| **官方** | HTTP → apiServer → MQTT → 设备 | apiServer 自带 Swagger +《应用开发指导手册》5.1/5.2 |
| **本项目** | `NcLink` 库 → MQTT → 设备 | 同上（实测校对，见 `official-simulator-verified-protocol.md`） |

## 环境

| 组件 | 说明 |
|---|---|
| EMQX | `<EMQX_HOME>`，监听 `127.0.0.1:1883` |
| 官方模拟器 | `StressTest-INC-Cloud.exe`，10 台 `STRESS_TEST_00000`~`00009` |
| apiServer | `nclink-api-server-0.3.10.jar`（JDK 17），`http://127.0.0.1:19001` |

apiServer 的 `application.yml` 需把 `mqtt.url` 指向 `tcp://127.0.0.1:1883`
（原始指向 `27.17.57.164`），并关闭 Eureka（避免连不存在的注册中心）。

## 官方 HTTP 契约（取自 apiServer 自带 Swagger）

```
DataRequest:  { items: Item[], operation: string, timeout: long }
Item:         { path, index, key, length, offset, value }
DataResponse: { code: int32, status: string, value: object }
```

端点：

| 方法 | 路径 | 说明 |
|---|---|---|
| GET/POST | `/v1/{device_id}/data/**` | 数据接口（`operation` 见下表） |
| GET | `/v1/{device_id}/file/**` | 文件读取 |
| POST | `/v1/{device_id}/file/**` | 下发文件 |
| GET | `/v1/{device_id}/model` | 取设备模型 |
| POST | `/v1/sample/{device_id}/sub/{channel_id}` | 订阅采样 |
| POST | `/v1/sample/{device_id}/unsub/{sub_id}` | 取消订阅 |
| GET | `/v1/sample/{device_id}/data/{sub_id}` | 取采样数据 |
| GET | `/v1/tools/device/active/info` | 设备在线状态 |

`operation` 取值与手册表 5-1 一致（7 个）：
`get_value` / `set_value` / `get_length` / `get_keys` / `get_attributes` / `add` / `delete`。

## 参数编码差异（实测）

`index` 字段在 apiServer 侧是 `Object`，实测行为：

| 请求 | 结果 |
|---|---|
| `index: 0` | 返回 1 个值 |
| `index: [0,1,2,3]` | 返回 4 个值 |
| `index: "0-7"` | **`FAILED code=4`** —— apiServer 不接受区间字符串 |
| `offset` + `length` | `[null]`（对数据项不生效） |

而本项目的 `DataItemRequest.At(path, 0, 7)` 生成区间字符串 `indexes: ["0-7"]`，
设备**接受**这种编码。两者是同一语义的两种编码：

```csharp
// apiServer 侧：显式索引数组
{ "path": "...@REG_X", "index": [0,1,2,3] }

// 本项目侧：区间字符串（设备认）
DataItemRequest.At(path, 0, 7)      // -> indexes: ["0-7"]
DataItemRequest.At(path, 0, 1, 2, 3) // -> indexes: ["0","1","2","3"]，与上面等价
```

**比对时必须用等价参数**，否则会得出"不一致"的错误结论。

## 验证结果（13/13 一致）

```
[1] 取设备模型
  [一致] 模型可取得              api=11241 字节
  [一致] 采样通道集合一致         api=[event_upload,sample_channel0,sample_channel1]
                                lib=[event_upload,sample_channel0,sample_channel1]

[2] 查询数据项 get_value（索引 0,1,2,3）
  [一致] 查询均成功
  [一致] 取值一致                api=[777,0,0,0]  lib=[777,0,0,0]

[3] 查询长度 get_length
  [一致] 长度一致                api=[512]        lib=[512]

[4] 设值 set_value
  [一致] 设值均受理
  [一致] 回读值一致              api=[777]        lib=[777]

[5] 模型路径解析
  [一致] REG_Y 也可按路径取到     values=[0,0,0,0]

[6] 采样（主题 Sample/{SN}/{channelId}）
  [一致] 库侧收到采样帧           通道=sample_channel0 项数=5
  [一致] 帧归属设备正确           frame.DeviceId=STRESS_TEST_00000
  [一致] 数据项映射来自模型通道配置 ItemIds=6 项, Items=5 项
  [一致] apiServer 侧也能订阅同一通道  sub_id=c57cf226-...
  [一致] apiServer 侧能取到采样数据    443 字符
```

## 运行方式

```powershell
# 1. 起 EMQX
<EMQX_HOME>\bin\emqx.cmd start

# 2. 起官方模拟器（config.yaml 的 Mqtt.IP 指向 127.0.0.1）
../厂家资料/NC-Link设备模拟器\StressTest-INC-Cloud.exe

# 3. 起 apiServer（application.yml 的 mqtt.url 指向本机）
java -jar ../厂家资料/nclink-api-server-0.3.10.jar

# 4. 跑一致性验证
dotnet run --project NcLink.Conformance
dotnet run --project NcLink.Conformance -- --sn STRESS_TEST_00001 --api http://127.0.0.1:19001
```

## 结论

**本项目与官方 apiServer 在同一设备上的结果完全一致（13/13）。**

这验证了：报文格式、`operation` 语义、路径解析、`index`/索引传递、
返回码处理、模型结构解析、**采样主题与归因**——两条独立实现路径得到相同结果。

采样项是补测加入的：此前实现把主题写成 `Sample/{channelId}`（少了 SN），
**订阅成功却永远收不到帧**，而自建模拟器犯同样的错、测试因此全绿。
只有拿官方 apiServer 做交叉验证才暴露出来。

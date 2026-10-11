# JSON 库选型评估：System.Text.Json vs Newtonsoft.Json

**结论：不换。** 换到 Newtonsoft.Json 会**同时**让线格式出错、代码变多、分配涨 4.5 倍。
下面是实测数据，不是印象。

评估动机：Newtonsoft.Json（Json.NET）stars 高、使用广泛，直觉上"用成熟的库能少写代码"。
这个直觉在**本项目这个场景**下不成立，原因很具体。

---

## 一、线格式会直接坏掉（最致命）

同一条查询请求报文，两个库的产出：

```
System.Text.Json : {"ids":[{"id":"MACHINE@1234"}],"@id":"dev-1"}
Newtonsoft.Json  : {"Ids":[{"Id":"MACHINE@1234","Params":null}],"CorrelationId":"dev-1"}
```

四处错误，每一处都是**对外契约**：

| 问题 | 原因 |
|---|---|
| `@id` → `CorrelationId` | Newtonsoft **不认** `[JsonPropertyName]`，那是 STJ 的特性 |
| `ids`/`id` → `Ids`/`Id` | 没有 camelCase 策略（我们靠 `[JsonPropertyName]` 逐个钉死）|
| 多出 `"Params":null` | Newtonsoft 默认 `NullValueHandling.Include` |
| **报文 45 字节 → 69 字节** | 空值没被忽略，报文膨胀 **53%** |

换库必须把 **38 处 `[JsonPropertyName]` 全改成 `[JsonProperty]`**，
并补上 `NullValueHandling.Ignore` + `CamelCasePropertyNamesContractResolver` 等设置。
**这是"改一堆标注"，不是"少写代码"。**

而协议字段名是与设备通信的契约，`NcLink.ApiServer` 的对比套件会逐字节比对
——改错的代价是设备不认。

---

## 二、没有字节级 API，丢掉刚建立的零分配路径

实测 Newtonsoft.Json 13.0.4（`netstandard2.0` 目标）的公开 API：

| API | Newtonsoft | System.Text.Json |
|---|---|---|
| `SerializeToUtf8Bytes` | ❌ | ✅ |
| `Deserialize(ReadOnlySpan<byte>)` | ❌ | ✅ |
| `Utf8JsonWriter` / `Utf8JsonReader` | ❌ | ✅ |
| `IBufferWriter` | ❌ | ✅ |

它的入口只有 `SerializeObject`（→ `string`）与 `DeserializeObject`（← `string`）。
收发一条报文必须多一次 **UTF-16 中转**——而那个中转正是本项目上一轮刚刚**实测删掉**的：

```
响应匹配   解码成字符串 480,000 字节/2000 次  →  字节级查找 0 字节
报文编码   经字符串   1,161,688 字节/2000 次  →  直接 UTF-8 736,000
```

换到 Newtonsoft 等于把这两处优化**原样还回去**。

### 编码分配的实测对照（同一条报文，2000 次）

```
System.Text.Json （SerializeToUtf8Bytes）   768,000 字节   →    384 字节/次
Newtonsoft.Json  （SerializeObject + 编码）3,488,000 字节   →  1,744 字节/次
```

**Newtonsoft 是 STJ 的 4.5 倍。**

---

## 三、"JToken 能让 NcJsonValue 变短"的假设也不成立

原本寄希望于 `JToken` 的类型强转能替掉 `NcJsonValue`（86 行）。
实测协议里真实的 8 种值形状：

| 输入 | 我们的 `NcJsonValue.ToInt32` | `JToken.ToObject<int>()` |
|---|---|---|
| `5` | 5 | 5 |
| `"5"` | 5 | 5 |
| `"5.9"` | **5**（截断） | **抛异常** |
| `true` / `false` | 1 / 0 | 需额外分支 |
| `null` | 0 | 需额外分支 |
| 数组 / 对象 | 抛 `FormatException` | — |

**`ToObject<int>()` 不做浮点字符串截断**，而设备会返回 `"5.9"` 这类值。
要保住现有语义（浮点字符串截断、布尔转 0/1、null 转 0），
`JToken` 版本**仍要写全部分支**——不会更短，可能更长。

---

## 四、其它维度的对比

| 维度 | System.Text.Json | Newtonsoft.Json |
|---|---|---|
| 许可证 | MIT ✅ | MIT ✅（这个不是问题）|
| 维护状态 | 一方、**活跃开发**（.NET 10 仍在增强）| **维护模式**（作者已加入 Microsoft 做 STJ）|
| AOT / 裁剪 | 源生成器 `JsonSerializerContext` ✅ | 反射为主，AOT 支持有限 |
| 性能 | .NET 8+ 起普遍领先 | 落后 |
| 依赖 | 框架内置，**零额外包** | 多一个第三方包（违背 §7 的单例外约定）|
| 异构 DOM | `JsonElement`（我们已封装成 `NcJsonValue`）| `JToken`（更顺手，但我们用不上）|

---

## 五、那"减少代码量"到底有没有空间？

有，但不在换库上。清点结果：**JSON 相关 10 个文件 / 1330 行**，
其中标注占大头——**38 处 `[JsonPropertyName]` + 19 处 `[JsonIgnore(WhenWritingNull)]`**。

那 19 处看起来与 `NcLinkJson.Options.DefaultIgnoreCondition = WhenWritingNull` 重复，
**但它们是刻意的契约固定**：这些协议类型是 public 的，消费者若用默认选项
`JsonSerializer.Serialize(msg)`（默认**不**忽略 null），逐属性的标注仍能保证线格式正确。
去掉它们等于把线格式交给调用方的选项。**保留。**

真正能省的地方在别处（见 `docs/core-audit.md`），不在 JSON 库。

---

## 复现方式

```csharp
// 一条真实报文
var msg = new QueryRequestMessage { CorrelationId = "dev-1",
    Ids = [new QueryRequestItemMessage { Id = "MACHINE@1234" }] };

NcLinkJson.Serialize(msg, msg.GetType());      // {"ids":[{"id":"..."}],"@id":"dev-1"}
JsonConvert.SerializeObject(msg);              // {"Ids":[{"Id":"...","Params":null}],"CorrelationId":"..."}
```

分配量用 `GC.GetAllocatedBytesForCurrentThread()` 在 `GC.Collect()` 后测
（与 `NcLink.Tests/Client/HotPathAllocationTests.cs` 同一套测法）。

---

## 什么情况下该重新评估

- 需要处理**大量**动态/异构 JSON（`JToken` 的便利性才划算）
- 目标框架不再内置 STJ（不可能）
- 需要用 Newtonsoft 生态特有的 Converter

本项目三条都不满足，因此**维持 System.Text.Json**。

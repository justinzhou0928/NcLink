# 第三方组件与许可证

本仓库以 **MIT** 发布（见 [LICENSE](LICENSE)）。下面是全部依赖及其许可证。

**结论：没有任何 copyleft 依赖。**

- 随 `NcLink` NuGet 包发布给消费者的依赖 —— **12 个包，全部 MIT**。
- `Apache-2.0` 只出现在测试项目（xunit），**不随包分发**。
- 全部第三方包只有 **两个**：`MQTTnet` 与 `Swashbuckle.AspNetCore.SwaggerUI`。
  其余都是 Microsoft 一方包。

> 许可证取自各包 NuGet 缓存里的 `.nuspec`（`<license>` / `<licenseUrl>`）与包内
> `LICENSE` 文件，是权威来源而非二手转述。
> 无论哪个包，只有在**重分发其二进制**时才需要附带许可证文本；本仓库通过
> NuGet 声明依赖、由消费者自行还原，因此各包适用其原始许可证，无附加义务。

---

## 一、随 `NcLink` 包发布的依赖（12 个，全部 MIT）

| 包 | 版本 | 许可证 |
|---|---|---|
| **MQTTnet** | 5.2.0.1603 | MIT（`.NET Foundation and Contributors`）|
| Microsoft.Extensions.Configuration | 10.0.12 | MIT |
| Microsoft.Extensions.Configuration.Abstractions | 10.0.12 | MIT |
| Microsoft.Extensions.Configuration.Binder | 10.0.12 | MIT |
| Microsoft.Extensions.DependencyInjection.Abstractions | 10.0.12 | MIT |
| Microsoft.Extensions.Diagnostics.Abstractions | 10.0.12 | MIT |
| Microsoft.Extensions.FileProviders.Abstractions | 10.0.12 | MIT |
| Microsoft.Extensions.Hosting.Abstractions | 10.0.12 | MIT |
| Microsoft.Extensions.Logging.Abstractions | 10.0.12 | MIT |
| Microsoft.Extensions.Options | 10.0.12 | MIT |
| Microsoft.Extensions.Options.ConfigurationExtensions | 10.0.12 | MIT |
| Microsoft.Extensions.Primitives | 10.0.12 | MIT |

**MQTTnet 是这里唯一的第三方（非 Microsoft）依赖**，也是唯一一个不在 nuspec 里
写 SPDX 表达式、而是随包附 `LICENSE` 文件的。已核对其包内 `LICENSE` 与上游仓库
[dotnet/MQTTnet 的 LICENSE](https://github.com/dotnet/MQTTnet/blob/master/LICENSE)，
两处都是 MIT。

---

## 二、仅用于可执行项目与测试（**不随包发布**）

| 包 | 版本 | 许可证 | 用在哪 |
|---|---|---|---|
| Microsoft.AspNetCore.OpenApi | 10.0.12 | MIT | `NcLink.ApiServer` |
| Microsoft.OpenApi | 2.12.0 | MIT | 同上（传递）|
| **Swashbuckle.AspNetCore.SwaggerUI** | 10.3.0 | MIT | 同上（只取 UI 静态资源，不含生成器）|
| Microsoft.Extensions.Hosting | 10.0.12 | MIT | `NcLink.Conformance`（工具）|
| Microsoft.Extensions.Hosting 的全部传递依赖<br>（Configuration.* / Logging.* / FileProviders.* / DependencyInjection / Diagnostics / System.Diagnostics.EventLog）| 10.0.12 | MIT | 同上 |
| Microsoft.Extensions.DependencyInjection | 10.0.12 | MIT | `NcLink.Tests` |
| Microsoft.Extensions.Logging | 10.0.12 | MIT | 同上 |
| **xunit** / xunit.assert / xunit.core / xunit.extensibility.* | 2.9.3 | **Apache-2.0** | 同上 |
| xunit.analyzers | 1.18.0 | **Apache-2.0** | 同上（传递）|
| xunit.abstractions | 2.0.3 | **Apache-2.0**（旧式 `licenseUrl`，指向 xunit 许可证）| 同上（传递）|
| **xunit.runner.visualstudio** | 4.0.1 | **Apache-2.0** | 同上 |
| coverlet.collector | 10.1.0 | MIT | 同上 |
| Microsoft.NET.Test.Sdk | 18.10.1 | MIT | 同上 |
| Microsoft.CodeCoverage / Microsoft.TestPlatform.* | 18.10.1 | MIT | 同上（传递）|

> **`Newtonsoft.Json` 曾经在这里**（`Microsoft.NET.Test.Sdk` 17.x 的传递依赖）。
> 升级到 18.10.1 后它**已从依赖图中消失**，本仓库现在完全不依赖它。

---

## 三、许可证相容性

| 许可证 | 是否 copyleft | 与本项目 MIT 相容 |
|---|---|---|
| MIT | 否 | ✅ |
| Apache-2.0 | 否（宽松，含专利授权条款）| ✅ |

两者都允许自由使用、修改、再分发，**无传染性**。
本仓库不包含 GPL / LGPL / AGPL / SSPL 等 copyleft 组件。

---

## 四、自行实现而非引库的部分

以下能力**没有**引入第三方库，而是按 `AGENTS.md §7` 的判据自行实现
（它们是协议特有的，市面上没有现成 C# 库）：

- 请求-响应关联（`RequestCorrelator`）
- 主题拼接与响应主题归属判定（`NcLinkTopics`）
- 十六进制 + 分块的文件传输（`FileTransfer`）
- 报文形状（`NcLinkMessages`）

JSON 用的是 .NET 内置的 `System.Text.Json`，**没有**引入 Newtonsoft.Json。
换用后者的实测代价见 [docs/json-library-evaluation.md](docs/json-library-evaluation.md)
（线格式出错、报文涨 53%、编码分配涨 4.5 倍）。

---

## 五、复核方式

```powershell
# 直接列出全部依赖（含传递），不看二手结论
dotnet list package --include-transitive

# 读某个包的许可证（权威来源）
#   ~/.nuget/packages/<id>/<version>/<id>.nuspec  →  <license> 或 <licenseUrl>
```

# AGENTS.md

本文件为 Codex(Codex.ai/code)在本仓库中工作时提供指引。

## 这是什么

TiaMcp 把西门子 **TIA Portal Openness (V21)** 封装为独立的 **MCP server**,让 agent 开发工具
(Codex、Cursor)能驱动 TIA 工程:连接、列出/读/导入/导出 块与 tag、编译、下载、运行/停车。
这里没有 LLM/generator/RAG 层——就是 MCP server。工具面与阶段状态在 `README.md`;本文件覆盖架构,
以及一次改动通常触及的非显然机制。

**架构(2026-07-23 重构为多品牌 monorepo):** 仓库按品牌 + 通用层组织。西门子 TIA 品牌单元在 `brands/tia/`:能力层 = MCP server,在 `brands/tia/mcp/`(`brands/tia/mcp/src/` + `brands/tia/mcp/TiaMcp.slnx` + `brands/tia/mcp/docs/`);品牌知识层 = `brands/tia/skills/`(渐进式 `SKILL.md`,英文命令名 + 中文正文)。品牌中性的通用方法论在仓库根 `skills/`(目前为桩);公司标准**不进本仓库**——`standards/` 只是挂载点,内容来自独立上层私有库(见 `standards/README.md`)。插件打包在 `plugins/`:`plugins/plc-siemens/`(TIA mcp + 品牌 skills)+ `plugins/plc-base/`(通用 skills,无 mcp)。编排层 = agent。详见 `docs/architecture.md`。
**构建命令都带 `brands/tia/mcp/` 前缀**(下文)。`plc/`、`refcode/` 是本地测试/参考(gitignore),不进插件包。

## 构建、运行、烟测

```bash
# 核心(4 个 net10 项目)——哪里都能构建,无需 TIA:
dotnet build brands/tia/mcp/TiaMcp.slnx

# 离线自检:直接驱动 Fake 后端(不走 MCP、不走 guard),然后退出:
dotnet run --project brands/tia/mcp/src/TiaMcp.Server -- --selftest

# stdio MCP 烟测——每个访问档一个场景(ReadOnly / ReadWrite / Unrestricted):
python brands/tia/mcp/tests/smoke_mcp.py brands/tia/mcp/src/TiaMcp.Server/bin/Debug/net10.0/TiaMcp.Server.dll

# HTTP 传输烟测(/mcp 常驻 server):
dotnet brands/tia/mcp/src/TiaMcp.Server/bin/Debug/net10.0/TiaMcp.Server.dll --transport http --backend fake --mode ReadWrite --urls http://127.0.0.1:5270
python brands/tia/mcp/tests/http_smoke.py

# net48 worker——不在 TiaMcp.slnx 里;单独构建,只在 TIA V21 机器上:
dotnet build brands/tia/mcp/src/TiaMcp.Openness.Worker/TiaMcp.Openness.Worker.csproj
```

没有单元测试工程,也没有 lint 配置。`brands/tia/mcp/tests/` 下的烟测 `.py` 脚本是事实上的测试套件。
`smoke_openness.py` / `smoke_openness_http.py` 跑**真** TIA 路径(慢,首次起 worker 约 30–90 秒)。
`--selftest` **只走 Fake**:传 `--backend openness` 它会打印一句说明然后照样跑 Fake 流程——要练
Openness 后端就走 MCP 协议。

标志:`--backend fake|openness`(或 `TIA_MCP_BACKEND`)、`--mode ReadOnly|ReadWrite|Unrestricted`
(或 `TIA_MCP_MODE`)、`--transport stdio|http`、`--workerPath <exe>`(或 `TIA_MCP_WORKER`)、
`--auditDir <dir>`。`plugins/plc-siemens/.mcp.json` 出厂带 `--backend openness --mode ReadWrite`。

## 架构:接缝、桥、worker

```
Agent ──MCP(stdio/http)──> TiaMcp.Server (net10) ──BridgeBackend──stdin/stdout JSON-RPC──> TiaMcp.Openness.Worker (net48) ──> Siemens.Engineering.*
```

- **`TiaMcp.Contract`**(net10 + netstandard2.0)——**单一接缝**。`ITiaBackend` 是 server 工具层唯一允许
  依赖的接口;它还持有所有 DTO、枚举、`RpcMessages` 线协议、`TiaPath` DSL。多目标到 netstandard2.0 使
  net48 worker 共享**完全相同的 DTO 类型**(一个 `IsExternalInit.cs` polyfill + 用 `Substring` 而非
  范围操作符保持 netstandard2.0 干净——此处不要引入 C# ranges/index)。
- **`TiaMcp.Server`**(net10)——MCP 宿主。`Tools/*Tools.cs` 很薄:解析参数、跑 `AccessGuard`、调
  `ITiaBackend`、包装结果。`Program.cs` 是组合根:选 transport + backend + mode 并注册。
  **工具类只引用 `TiaMcp.Contract`**——绝不引用 Siemens,也不直接引用桥类型。
- **`TiaMcp.Fake`**(net10)——内存后端;无 TIA 跑全量(dev/CI/dry-run)。
- **`TiaMcp.Openness`**(net10)——`BridgeBackend : ITiaBackend`。spawn net48 worker,经 `WorkerChannel`
  把**每个**调用作为一次 stdin/stdout JSON-RPC 往返转发。一个 `SemaphoreSlim(1,1)` 串行化调用,
  因为 worker 持单个**非线程安全**的 `TiaPortal`。引用 **0** 个 Siemens 程序集。
- **`TiaMcp.Openness.Worker`**(net48)——**唯一**加载 `Siemens.Engineering.{Base,Step7}` 的进程
  (V21 仅 net48,且 V21 把旧的单 DLL 拆成了两个)。`OpennessEngine.cs` 持已验证的 Siemens 调用;
  `Program.cs` 是逐行 stdin/stdout JSON-RPC 循环。**故意不进 `TiaMcp.slnx`**,这样在没有 net48 dev pack
  + Siemens DLL 的机器上解决方案仍绿。

**加新工具 / 后端操作是这里最常见的改动。** 完整路径:在 `ITiaBackend` 加方法 → 在 `FakeBackend` 和
`OpennessEngine` 实现 → 在 `RpcMessages.cs` 加 `RpcOp` + 参数/结果 record → 在 `BridgeBackend` 转发 →
在相关 `*Tools.cs` 加 `[McpServerTool]`。若改状态,还要加 `TiaOps` 常量并在 `AccessGuard` 选其档位,
并在工具里审计记录。做这事时你会触及全部四个 net10 工程 + worker。

## 安全模型(在 `TiaMcp.Server/Safety/`)

`AccessGuard.Check(op, confirm)` 是**纯策略**——它不修改状态、不审计;两者都由工具在后端调用前后做。
档位,都按 `TiaOps` 字符串常量索引:

- **不受限操作**(`download`、`plc_run`、`plc_stop`):需 `--mode Unrestricted` **且** `confirm=true`。仅真机。
- **写操作**(导入/创建 块与 tag、在线 连接/断开、全部项目生命周期、加/配网络设备):需 `ReadWrite`+。
- **破坏性操作**(`delete_block`、`delete_tag`):需 `ReadWrite`+ **且** `confirm=true`。

不带 `confirm=true` 的破坏性/不受限调用返回 `MutationResult{status=AwaitingConfirmation}` + 重新调用提示,
什么都不做。guard **允许**的每次变更都追加到审计日志(JSONL,默认
`%TEMP%/tiamcp-audit/<date>.jsonl`);拒绝/预览不记录。

⚠️ **默认模式是 `ReadWrite`,不是 `ReadOnly`。** `Program.cs` 在 `--mode` 未设时回退到
`AccessMode.ReadWrite`,而 `AccessMode` 枚举的文档注释("Default is ReadOnly")已过时。以代码为准。
(`--selftest` 完全绕过 guard。)

## 错误透明

只读工具绝不向客户端抛异常:它们走 `ToolErrors.InvokeAsync`,成功返回 DTO、失败返回 `ToolError`。
写工具总是返回结构化的 `MutationResult`。这是刻意的——MCP 客户端会把原始异常掩盖成通用的
"An error occurred invoking …",没有它真因(一个 Openness worker 错误、未找到、V21 `NotSupportedException`)
就到不了 agent。`ToolErrors.ToError` 遍历内部异常,在桥的 "Openness worker error (Type): msg" 包装下浮出
真消息。**不要让只读工具抛异常**;走 `ToolErrors`。

## 关键坑

- **stdio 的 stdout 是神圣的。** `Program.cs` 在两种传输下都调 `Logging.ClearProviders()`,让 stdout
  **只**承 JSON-RPC 帧。stdio 下绝不加往 stdout 写的 logger(用 stderr)。worker 也守同样规则——它的
  `Console.Error.WriteLine` 诊断是故意的;桥也把状态失败记到 stderr。
- **并发派发。** MCP SDK 并发派发请求,响应乱序到达(按 `id` 匹配,不按序号)。`BridgeBackend` 的
  每调用 `SemaphoreSlim` 保护单个 worker `TiaPortal`;改 channel 时要保住这个串行化。
- **Siemens DLL 解析(只关乎 worker / 真 TIA)。** Siemens DLL 是 **Copy-Local=False**
  (`<Private>False</Private>`)且本安装**不在 GAC**。它们经 `<codeBase>` 提示加载进**默认加载上下文**
  ——这是关键所在,因为若 DLL 从拷来的位置或经 `Assembly.LoadFrom`(load-from 上下文)加载,Siemens 的
  `OpennessLocationProvider` 会在 `TiaPortal` 握手时抛 `EngineeringSecurityException`。`Program.cs` *还*
  注册了一个 `AssemblyResolve` fallback(LoadFrom)作为最后手段,但**真正让握手通过的是 codeBase。**
  两层设定那个 href:
  1. **构建期**——worker 的 `.exe.config` 由 `GenerateWorkerAppConfig` target 从 `App.config.template`
     生成,把 csproj 的 `$(TiaInstallDir)` 注入 `<codeBase>` href(默认烤进本机的 `D:\…\Portal V21`;
     用 `-p:TiaInstallDir=…` 覆盖)。改 XML 形状就改模板;**不要**手改生成的 `obj\…\App.generated.config`。
  2. **运行期(到处部署的关键)**——spawn worker 前,`WorkerChannel.PatchWorkerCodeBase` 从注册表
     (`HKLM\SOFTWARE\Siemens\Automation\Openness\…\net48`,Siemens 自己的发现键)读**真实** net48 目录,
     并改写 worker 的 `.exe.config` `<codeBase>` href 与之匹配。所以一个发布包在任何 V21 机器上都能跑,
     不管 TIA 装在哪;构建期值只是 fallback。2026-06-24 验证:故意烤错路径,桥从注册表自动纠正,
     headless `TiaPortal` 握手通过。
  部署细节在 `docs/deployment.md`。
- **net48 worker 一次性前置**(仅真 TIA):Windows 用户必须在 `Siemens TIA Openness` 本地组(否则
  `Open()`/`Attach()` 抛 `COMException`),且必须装 **.NET Framework 4.8 Developer Pack**(不是运行时)
  才能构建。
- **挂接优于 spawn。** 真 TIA 机器上优先用 `tia_connect` 的 `mode=attach` 复用已开的 Portal——避开
  spawn 实例的锁/释放麻烦。
- **`nul` 与根目录杂散 `.*` 文件**是临时脚本/产物,不是源码。

已验证的 Siemens API 参考(确切调用、V21 net48-vs-net6 勘误、源码链接)在
`brands/tia/mcp/docs/P3-openness-notes.md`——改 `OpennessEngine.cs` 前先读它。

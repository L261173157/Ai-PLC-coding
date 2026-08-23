# Ai-PLC-coding —— 多品牌 PLC MCP 工程套件

把多个 PLC 品牌的工程软件封装成 **MCP server**,配以**品牌中性的通用开发方法论 skills**,
打包成 Claude Code 插件,让 agent(Claude Code、Cursor)能驱动 PLC 工程:
连接、组态硬件、读写块/变量、编译、下载。**已实现**:西门子 TIA Portal(Openness V21);
**规划中**:Rockwell、Mitsubishi。

> **三层架构**(能力 / 知识 / 编排,详见 [`docs/architecture.md`](docs/architecture.md))+ 公司标准 + 插件打包:
> - **能力层**(各品牌 MCP server,机制):`brands/<brand>/mcp/`。西门子 TIA → [`brands/tia/mcp/`](brands/tia/mcp/)(工具见下)。
> - **知识层**(skills,策略):品牌工程手册 `brands/<brand>/skills/`(TIA → [`brands/tia/skills/`](brands/tia/skills/),先读 [`overview`](brands/tia/skills/overview/SKILL.md))+ 品牌中性通用方法论 [`skills/`](skills/)。
> - **编排层**(agent):不在本仓库实现。
> - **公司标准**(叠在知识层上):[`standards/`](standards/) 是**挂载点**——本仓库不含任何公司标准;公司标准住在独立的上层私有库,复制/链接到 `standards/<company>/` 后生效,见 [`standards/README.md`](standards/README.md)。
> - **插件打包**(能力 + 知识的交付):[`plugins/`](plugins/)(`plc-base` 通用 + 每品牌一个,如 `plc-siemens`)。
>
> 部署见 [`docs/deployment.md`](docs/deployment.md)。本仓库交付"能力 + 知识 + 插件",不含 LLM/generator/RAG 层。

---

以下为**已实现的西门子 TIA 品牌(TiaMcp)**详情。

## 工具

所有工具都**按路径寻址**:传一个路径如
`session:s-fake/project:Demo/device:PLC_1/plc:program/block:FB_Motor`。裸 session 路径
自动解析到其第一个 PLC。档位标记:✏️ = 写(需 `--mode ReadWrite`),
🔥 = 破坏性(需 `--mode ReadWrite` **且** `confirm=true`),
⚠️ = 真机(需 `--mode Unrestricted` **且** `confirm=true`);无标记 = 读。

### 会话与系统

| 工具 | 用途 |
|---|---|
| `tia_status` | 后端类型、TIA 版本、访问模式、打开的会话数 |
| `tia_connect` | 启动/挂接 TIA 会话 → 会话 id + 根路径 |
| `tia_disconnect` | 释放会话(释放 Portal 进程;任意模式都安全) |

### 项目生命周期

| 工具 | 用途 |
|---|---|
| `tia_project_open` | 打开 `.ap19/.ap21` → 项目路径 |
| `tia_project_list` | 列出项目内设备目标(PLC/HMI) |
| `tia_project_compile` | 编译(Software/Hardware/All)→ 结构化诊断 |
| `tia_project_status` | 项目元信息:名/路径/版本/作者、是否改动、大小 |
| `tia_project_save` ✏️ | 保存项目到磁盘 |
| `tia_project_save_as` ✏️ | 另存为新目录/名(可选重绑) |
| `tia_project_create` ✏️ | 新建空项目 → 新路径 |
| `tia_project_archive` ✏️ | 归档为 `.zap1x` 文件 |
| `tia_project_close` ✏️ | 关闭项目(可选先保存) |

### 块(程序)

| 工具 | 用途 |
|---|---|
| `tia_block_list` | 列出某作用域下的块(OB/FB/FC/DB/UDT),分页 |
| `tia_block_info` | 块头(名/类型/号/语言/注释) |
| `tia_block_read_code` | 块体紧凑结构化视图:LAD=布尔表达式+线圈+盒清单 / SCL=拍平文本 / GRAPH=步转移视图 |
| `tia_block_read_source` | 块源码(Openness 下为 SimaticML XML) |
| `tia_interface_read` | 结构化接口成员树(块/UDT) |
| `tia_udt_list` | 列 PLC 数据类型(UDT),递归类型组 |
| `tia_cross_reference` | 交叉引用(被谁用/用了谁,访问类型) |
| `tia_block_export` | 导出块为 `.scl` / SimaticML `.xml` |
| `tia_block_import` ✏️ | 从 SimaticML XML 导入块(存在则覆盖) |
| `tia_block_write_code` ✏️ | 结构化 JSON spec → SimaticML → 导入(LAD 标准集;dryRun 只出 XML) |
| `tia_block_delete` 🔥 | 删除块 |

### 变量(tags)

| 工具 | 用途 |
|---|---|
| `tia_tag_list` | 列出某作用域下的 PLC tag,分页 |
| `tia_tagtable_list` | 列 PLC 变量表(含 tag 数) |
| `tia_tagtable_export` | 导出变量表(+ 其 tags)为 SimaticML `.xml` |
| `tia_tag_create` ✏️ | 建 PLC tag |
| `tia_tag_delete` 🔥 | 删除 tag |

### 硬件(设备/网络)

| 工具 | 用途 |
|---|---|
| `tia_device_item_list` | 列某设备的硬件项(机架/模块、槽位、订货号) |
| `tia_catalog_search` | 搜硬件目录 → typeIdentifier |
| `tia_hardware_read` | 读项目硬件:设备 + 子网/IO 系统 |
| `tia_device_add` ✏️ | 从目录 typeIdentifier 建设备 |
| `tia_network_configure` ✏️ | 设 IP/掩码,建+连子网与 IO 系统 |
| `tia_module_add` ✏️ | 往机架槽位插信号/通信模块 |
| `tia_cpu_system_clock_memory` ✏️ | 配 CPU 系统/时钟存储字节 |
| `tia_device_delete` 🔥 | 删设备/站 |
| `tia_module_delete` 🔥 | 删已插模块 |
| `tia_subnet_delete` 🔥 | 删子网 |

### 导入与分组

| 工具 | 用途 |
|---|---|
| `tia_udt_import` ✏️ | 从 SimaticML XML 导入 UDT |
| `tia_tagtable_import` ✏️ | 从 SimaticML XML 导入变量表(+ tags) |
| `tia_block_generate_from_source` ✏️ | 从 SCL/AWL 源文本生成块(ExternalSources) |
| `tia_group_create` ✏️ | 建组织文件夹(block/type/tagtable) |

### 库复用

| 工具 | 用途 |
|---|---|
| `tia_library_open` ✏️ | 打开全局库(`.al21`)用于母版复用 |
| `tia_mastercopy_list` | 列已开库内的母版 |
| `tia_block_create_from_copy` ✏️ | 把母版实例化为新块 |

### 在线(真 PLC)

| 工具 | 用途 |
|---|---|
| `tia_online_status` | 读 PLC 在线状态(在线/离线、RUN/STOP) |
| `tia_online_connect` ✏️ | 在线连接 PLC |
| `tia_online_disconnect` ✏️ | 断开在线连接 |
| `tia_download` ⚠️ | 下载 HW/SW 到 PLC |
| `tia_plc_run` ⚠️ | PLC 置 RUN |
| `tia_plc_stop` ⚠️ | PLC 置 STOP |

## 安全模型(P2)

server 以某**访问模式**运行(`--mode`,默认 `ReadWrite`):

| 模式 | 允许 |
|---|---|
| `ReadOnly` | 所有读 + 编译;**不可写** |
| `ReadWrite`(默认) | + 导入/创建/删除 块、tag、设备/模块/子网;硬件添加与网络配置;项目 保存/新建/归档/关闭;库与 UDT/变量表导入;在线 连接/断开 |
| `Unrestricted` | + **下载**、plc run/stop(都需 `confirm=true`)—— 真机 |

- `ReadOnly` 下的写操作返回 `status=Denied` 的 `MutationResult`,什么都不做。
- 破坏性工具(`tia_block_delete`、`tia_tag_delete`、`tia_device_delete`、
  `tia_module_delete`、`tia_subnet_delete`)需 `confirm=true`;不传则返回
  `status=AwaitingConfirmation` + 计划 + 重新调用提示——绝不执行。
- 不受限工具(`tia_download`、`tia_plc_run`、`tia_plc_stop`)额外需
  `--mode Unrestricted` **且** `confirm=true`。
- guard 允许的每次变更都追加到**审计日志**(JSONL,默认
  `%TEMP%/tiamcp-audit/<date>.jsonl`);拒绝/预览不作为变更记录。

## 阶段状态

| 阶段 | 内容 | 状态 |
|---|---|---|
| **P0** | 解决方案 + 项目、stdio MCP 宿主、Fake 后端、`tia_status` / `tia_connect` / `tia_block_list` | ✅ 完成 |
| **P1** | PLC 读路径:`tia_project_open/list/compile`、`tia_block_info/read_source/export`;路径寻址落地 | ✅ 完成 |
| **P2** | PLC 写路径:`block/tag import/create/delete` + 安全(AccessMode + confirm + audit) | ✅ 完成 |
| **P3** | **真 Openness:net48 worker + net10 桥。** `TiaMcp.Openness.Worker`(net48)持一个 `TiaPortal`,服务已验证的 Siemens API(connect/open/list-targets/compile、blocks 读/写/导出、tags 读/写);`BridgeBackend`(net10)spawn 它并把每个 `ITiaBackend` 调用经 stdin/stdout JSON-RPC 转发。**2026-06-17 对真 TIA V21 端到端验证**:connect → 打开 `.ap21` → 列目标 → `tia_block_list` → `tia_block_read_source` → `tia_project_compile`(成功,0 错误)。在线/下载/设备项 = P4-real。见 `brands/tia/mcp/docs/P3-openness-notes.md`。 | ✅ 完全验证(读 + 编译) |
| **P4** | 在线/下载/run/stop 工具(Unrestricted 档,Fake 模拟)、设备项可见性、**HTTP 传输**。stdio + HTTP 验证过。真在线/下载(IPlcWebApi)= P4-real(需 TIA)。 | ✅ 完成 |

## 项目

| 项目 | Target | 角色 |
|---|---|---|
| `TiaMcp.Contract` | net10.0;netstandard2.0 | 接口 + DTO + 路径 DSL + 桥↔worker RPC 消息。**绝不**引用 Siemens。多目标使 net48 worker 共享相同类型。 |
| `TiaMcp.Fake` | net10.0 | 内存后端——无需 TIA 跑全量(dev/CI/dry-run)。 |
| `TiaMcp.Openness` | net10.0 | **桥后端**(`BridgeBackend`)——spawn net48 worker,经 stdin/stdout JSON-RPC 转发 `ITiaBackend` 调用。引用 **0** 个 Siemens 程序集。 |
| `TiaMcp.Openness.Worker` | **net48** | **唯一**加载 `Siemens.Engineering.*` 的进程(TIA V21 仅 net48)。持一个 `TiaPortal`;服务已验证的 Siemens API。**不在 `TiaMcp.slnx`**——只在有 net48 dev pack 的 TIA 机构建。 |
| `TiaMcp.Server` | net10.0 | MCP server(**stdio + HTTP**)。工具层只依赖 `Contract`;`Program.cs` 是组合根,通过 `--backend` 接后端、`--transport` 接传输。 |

铁律:**`TiaMcp.Server` 工具类只依赖 `TiaMcp.Contract`。** 后端是注入的,所以 server 在 0 TIA
安装下也能编译运行。真 Openness 代码在 net48 worker;net10 server 只通过桥的 JSON-RPC 管道访问它。

```
Agent ──MCP(stdio/http)──> TiaMcp.Server (net10) ──BridgeBackend──stdin/stdout JSON-RPC──> TiaMcp.Openness.Worker (net48) ──> Siemens.Engineering
```

## 构建与烟测

```bash
# 核心(4 个 net10 项目)——哪里都能构建,无需 TIA:
dotnet build brands/tia/mcp/TiaMcp.slnx

# 离线自检(直接驱动 Fake 后端,不走 MCP):
dotnet run --project brands/tia/mcp/src/TiaMcp.Server -- --selftest

# 完整 MCP 协议烟测——三档访问(ReadOnly / ReadWrite / Unrestricted 在线):
python brands/tia/mcp/tests/smoke_mcp.py brands/tia/mcp/src/TiaMcp.Server/bin/Debug/net10.0/TiaMcp.Server.dll

# HTTP 传输烟测(起常驻 server,驱动 /mcp):
dotnet brands/tia/mcp/src/TiaMcp.Server/bin/Debug/net10.0/TiaMcp.Server.dll \
  --transport http --backend fake --mode ReadWrite --urls http://127.0.0.1:5270 &
python brands/tia/mcp/tests/http_smoke.py
```

**net48 worker 故意不在 `TiaMcp.slnx` 里**(它需要 net48 dev pack + Siemens
DLL)。单独构建,只在 TIA V21 机器上(见下"真 TIA V21 配置"):

```bash
dotnet build brands/tia/mcp/src/TiaMcp.Openness.Worker/TiaMcp.Openness.Worker.csproj
```

## 真 TIA V21 配置

要驱动真 TIA V21 工程(非 Fake),TIA 机器需三个一次性前置,然后构建 worker 并以
`--backend openness` 跑 server:

1. **`Siemens TIA Openness` 本地组。** Openness 的 `TiaPortal.Open()` 在用户不在该组时抛
   `COMException`。加自己进去(管理员 PowerShell),然后**注销/重登**刷新令牌:
   ```powershell
   Add-LocalGroupMember -Group "Siemens TIA Openness" -Member "<DOMAIN>\<user>"
   ```
2. **.NET Framework 4.8 Developer Pack** —— 提供 net48 引用程序集以构建 worker。下离线安装器
   (*Developer Pack*,不是运行时):
   <https://dotnet.microsoft.com/download/dotnet-framework/thank-you/net48-developer-pack-offline-installer>
3. **安装路径。** worker csproj 默认 `D:\Program Files\Siemens\Automation\Portal V21`
   (`PublicAPI\V21\net48\`)。不同则 `-p:TiaInstallDir=...` 覆盖。

然后:
```bash
# 构建 worker(net48)——产出 TiaMcp.Openness.Worker.exe
dotnet build brands/tia/mcp/src/TiaMcp.Openness.Worker/TiaMcp.Openness.Worker.csproj

# 对真 TIA 跑 server。首次用时桥会 spawn worker。
dotnet run --project brands/tia/mcp/src/TiaMcp.Server -- --backend openness --mode ReadWrite
```

桥默认在 `brands/tia/mcp/src/TiaMcp.Openness.Worker/bin/Debug/net48/TiaMcp.Openness.Worker.exe`
找 worker;用 `--workerPath <exe>` / 环境变量 `TIA_MCP_WORKER` 覆盖(如 Release 构建或发布位置)。

> **DLL 解析(关键,血泪教训)。** Siemens DLL 在本安装**不在 GAC**,且**绝不能**拷到 worker exe
> 旁——Siemens 的 `OpennessLocationProvider` 会在 `TiaPortal` 握手时以 `EngineeringSecurityException`
> 拒绝拷来的程序集。worker 的 `.exe.config` 带 `<codeBase>` 提示,从 `PublicAPI\V21/net48` 以
> **默认加载上下文**加载它们,这才是握手通过的关键(`Assembly.LoadFrom` fallback 用错上下文会失败)。
> 该配置**构建期**从 `App.config.template` 生成,href 从 csproj 的 `TiaInstallDir` 注入——所以要适配
> 另一台机器,用 `-p:TiaInstallDir=<其 Portal V21 目录>` 构建(或改部署后 `.exe.config` 的 `href`)。
> 见 `docs/deployment.md`。

## 接入 Claude Code

西门子 TIA 品牌打包成 **Claude Code 插件**,在 `plugins/plc-siemens/`(`.claude-plugin/plugin.json`
+ `.mcp.json` 跨目录指向 `brands/tia/mcp/` server + `brands/tia/skills/` 手册)。加载插件会自动启动
`tia-mcp` MCP server(真 Openness 后端,`ReadWrite`),并把工程技能暴露为 `/tia-mcp:<skill>`。

**加载**(仓库根执行;首次或 skills 改动后先跑 `./scripts/dev-sync-plugins.ps1` 把 skills 同步进插件目录):
```bash
claude --plugin-dir plugins/plc-siemens
```
`/mcp` 可见 `tia-mcp`;`/tia-mcp:overview`(及其余 5 个技能)可用。需 TIA V21 机器——一次性前置见
"真 TIA V21 配置"(Siemens Openness 组 + 构建 net48 worker)。

插件 MCP 配置(参考;非 TIA 机器切 Fake 后端):
```json
{
  "mcpServers": {
    "tia-mcp": {
      "command": "dotnet",
      "args": ["run", "--project", "${CLAUDE_PLUGIN_ROOT}/../../brands/tia/mcp/src/TiaMcp.Server/TiaMcp.Server.csproj", "--", "--backend", "openness", "--mode", "ReadWrite"]
    }
  }
}
```
> **没装 TIA?** 把 `plugins/plc-siemens/.mcp.json` 改成 `--backend fake`(内存,哪都能跑),或
> 跑 `dotnet run --project brands/tia/mcp/src/TiaMcp.Server -- --selftest`。

经 marketplace 分发:在 TIA V21 机上 `scripts/build-plugin.ps1`(版本号缺省取自源 `plugin.json`)
产出自包含插件 `dist/plugins/plc-siemens/`,再 `scripts/package-marketplace.ps1` 拢成市场包
(`dist/tia-marketplace/` + `dist/tia-plugins-<v>.zip`)。**zip 不能直接当 marketplace**——消费者解压后
用 `claude` CLI(**不是** `/plugin` 斜杠命令):`claude plugin marketplace add <目录>` →
`claude plugin install tia-mcp@tia-plugins` → 重启 Claude Code(无需 .NET SDK)。
**升级**(把新 build 覆盖到已登记的市场目录后):`claude plugin marketplace update tia-plugins` →
`claude plugin update tia-mcp@tia-plugins` → 重启。仓库里的 `plugins/plc-siemens/` 只用于就地加载
(dev 版 `.mcp.json` 爬仓库目录树,复制进缓存后路径会断)。完整安装/升级/排错见 `docs/deployment.md`。

真 **TIA V21** 机器上,优先用 attach 模式(`tia_connect` 的 `mode=attach`)复用已开的 Portal——避开
spawn 实例的锁/释放麻烦。agent 即可如:
`tia_status` → `tia_connect` → `tia_project_open <你的.ap21>` → `tia_block_list` →
`tia_block_read_source` / `tia_project_compile`。

## 后端、模式与传输选择

`--backend fake`(默认)| `--backend openness`。也认环境变量 `TIA_MCP_BACKEND`。
`--mode ReadWrite`(默认)| `ReadOnly` | `Unrestricted`。也认 `TIA_MCP_MODE`。
要让 agent 写块/tag,用 `--mode ReadWrite` 跑(如改 `plugins/plc-siemens/.mcp.json`);下载/run/stop 用
`Unrestricted`。
`--transport stdio`(默认)| `http`。**stdio**:agent 把 server 当子进程拉起。
**http**:常驻共享 TIA 会话——多个 agent 客户端 POST JSON-RPC 到 `/mcp`
(如 `--transport http --urls http://127.0.0.1:5270`)。TIA 整天开着时用 http。

## 架构注意事项(内嵌的坑)

1. **stdio 的 stdout 是神圣的。** 默认 host builder 会挂一个往 stdout 写的控制台日志,污染 JSON-RPC
   流。`Program.cs` 调 `builder.Logging.ClearProviders()` 让 stdout 只承 MCP 消息。stdio 下别加 stdout 日志。
2. **MCP SDK 并发派发请求**——响应可能乱序,按 `id` 匹配而非序号。P3+ 含义:单个 `TiaPortal` **非**
   线程安全,故后端调用须按会话串行(每会话一个 `AsyncLock`)。`FakeBackend` 读是不可变的,P0 本身安全。
3. **HTTP 传输注册是 `WithHttpTransport()`**(streamable HTTP)+ `app.MapMcp("/mcp")`,在
   `ModelContextProtocol.AspNetCore` 1.4.0 SDK——不是 `WithStreamableHttpServerTransport`
   (该名在 1.4.0 不存在)。客户端须在后续请求带 `Mcp-Session-Id`(来自 initialize 响应),
   并接受 `application/json` 或 `text/event-stream`。

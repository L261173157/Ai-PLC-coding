# 部署:把能力层(MCP)+ 知识层(Skills)交给别人用

两样东西要交付:**能力层(MCP server,要装在有 TIA 的机器上)** 和 **知识层(brands/tia/skills/,纯 markdown,随便拷)**。

## 一、能力层:独立部署 MCP server

### 目标机前置
- Windows 10/11 + **TIA Portal V21**(安装时启用 Openness)。
- 当前用户加入本地组 **`Siemens TIA Openness`**(加完注销/重登)。
- **.NET Framework 4.8**(Win10/11 自带,也是 TIA 前置)。
- 运行时无需单独装 .NET 10(发布包自带)。

### 构建发布包(在一台有 TIA V21 的开发机上)
net48 worker 不在 `TiaMcp.slnx` 里,要先单独构建,再发布 server(发布时 `BundleOpennessWorker` target 会把 worker 拷进发布目录):

```
dotnet build brands/tia/mcp/src/TiaMcp.Openness.Worker -c Release
dotnet publish brands/tia/mcp/src/TiaMcp.Server -c Release -r win-x64 --self-contained true -o C:\dist\TiaMcp
```
目标机 TIA 装在哪都行——bridge 启动 worker 前会从注册表自动校正 codeBase(见下文「TIA 安装路径」)。无需为不同目标机分别出包。`-p:TiaInstallDir=...` 只是给 codeBase 一个**初始默认值**,自动发现会在运行时覆盖它。

得到自包含目录(约 109 MB),结构:
```
C:\dist\TiaMcp\
  TiaMcp.Server.exe
  [.NET 10 运行时 + 依赖]
  openness-worker\
    TiaMcp.Openness.Worker.exe
    TiaMcp.Openness.Worker.exe.config   # 构建期从 App.config.template 生成,<codeBase> 指向目标机 TIA 路径
    [net48 依赖]
```
把 `C:\dist\TiaMcp` 打包(zip)发给目标用户。**不要**把 Siemens.Engineering.* DLL 打进去——worker 在目标机从其本地 TIA 安装加载(`OpennessLocationProvider` 拒绝拷贝来的 Siemens DLL)。

### TIA 安装路径:**默认全自动,不用知道对方目录**
你**不需要**事先知道对方把 TIA 装在哪。net10 server(bridge)在每次启动 worker **之前**,会从注册表读出本机 TIA net48 API 目录,自动把 worker `.exe.config` 的 `<codeBase>` 改成正确路径(`WorkerChannel.PatchWorkerCodeBase`)。所以**同一个发布包丢到任何装了 V21 的机器上都能跑**,无论装在 C/D/E、默认还是自定义目录。

- 权威来源(Siemens 自己的发现机制):`HKLM\SOFTWARE\Siemens\Automation\Openness\<ver>\PublicAPI\<asmver>\net48` 的 `Siemens.Engineering.Base` 值,直接给出 DLL 全路径。
- 自愈成功时 worker stderr 会打印:`[bridge] patched worker codeBase -> <真实目录>`。
- 这一步只读注册表 + 改一个本地文本文件,**不需要管理员权限**。

> 为什么必须改 codeBase 而不能靠运行时 fallback:Siemens 的 `OpennessLocationProvider` 要求 DLL 从**默认加载上下文**加载,只有 `<codeBase>` 满足;`Program.cs` 里的 `AssemblyResolve`(`Assembly.LoadFrom`,load-from 上下文)会被以 `EngineeringSecurityException` 拒绝。而 codeBase 在进程**启动瞬间**就被 CLR 读取、worker 自己改不了,所以由 bridge 在 spawn 前改。

**手动兜底(仅当注册表里没有 V21 条目时才需要):**
- 构建期固定:`-p:TiaInstallDir=<目标机 Portal V21 目录>`(见上一节),把正确路径烤进发布包。
- 发布后改一行:`openness-worker\TiaMcp.Openness.Worker.exe.config` 是纯文本,直接改两处 `<codeBase href>`。
- 环境变量 `TIA_MCP_INSTALL_DIR`:**只喂给 `AssemblyResolve` fallback,不改 codeBase**,单独设它一般不足以让握手通过——优先靠上面的自动发现。

> 拿不准对方目录时,让对方跑一行确认安装位置:
> `reg query "HKLM\SOFTWARE\Siemens\Automation\Openness\21.0\PublicAPI\21.0.0.0\net48" /v Siemens.Engineering.Base`

### 让 agent 连上
任意支持 MCP 的客户端,指向发布的 server exe:
```json
{"mcpServers":{"tia":{"command":"C:\\Tools\\TiaMcp\\TiaMcp.Server.exe","args":["--backend","openness","--mode","ReadWrite"]}}}
```
(改 `ReadOnly` 更安全;worker 自动定位。)也支持 HTTP 常驻:`--transport http --urls http://127.0.0.1:5270`,端点 `/mcp`。

## 二、知识层:分发 skills

`brands/tia/skills/` 是纯中文 markdown(每个 skill = `<eng>/SKILL.md` + 按需 `_reference/`/`examples/`/`scripts/`),随插件分发。让 agent 在做 PLC 任务前先读总览 skill(`brands/tia/skills/overview/SKILL.md`)找到对应 skill。

## 三、本机开发自测
```
dotnet build brands/tia/mcp/TiaMcp.slnx
dotnet run --project brands/tia/mcp/src/TiaMcp.Server -- --selftest          # Fake 离线自测
python brands/tia/mcp/tests/smoke_mcp.py brands/tia/mcp/src/TiaMcp.Server/bin/Debug/net10.0/TiaMcp.Server.dll   # stdio 三档烟测
```

## Claude Code 插件(多品牌 monorepo)
西门子 TIA 品牌的插件在 `plugins/plc-siemens/`:`.claude-plugin/plugin.json` + `.mcp.json`(用 `${CLAUDE_PLUGIN_ROOT}/../../brands/tia/...` 跨目录指向源)+ `brands/tia/mcp/`(server 源码)+ `brands/tia/skills/<eng>/SKILL.md`(渐进式)。通用方法论另在 `plugins/plc-base/`(仅 skills)。
- **本机开发/试用**:`claude --plugin-dir plugins/plc-siemens`(在仓库根执行)→ 插件加载,`tia-mcp` 服务自动起,skills 变 `/tia-mcp:<eng>`。(dev 下品牌 skills 需先同步进插件目录,见 `plugins/plc-siemens/README.md`。)
- **marketplace 分发(自包含插件)**:在 TIA V21 机器上跑 `scripts/build-plugin.ps1`(版本号缺省自动取自源 `plugin.json`,也可 `-Version <v>` 覆盖),产出 `dist/plugins/plc-siemens/`(自包含:`server/`(含 `openness-worker/`)+ `skills/` + publish 模式 `.mcp.json` 直指 `server/TiaMcp.Server.exe`,不靠跨目录爬层)。再跑 `scripts/package-marketplace.ps1` 把它拢进市场根 `dist/tia-marketplace/`(`.claude-plugin/marketplace.json` + `plc-siemens/`)并压成 `dist/tia-plugins-<v>.zip`。⚠️ **zip 不能直接当 marketplace**——Claude Code 的 marketplace 必须是带 `marketplace.json` 的目录/git 仓库/URL,不是裸 zip。消费者解压后用本地路径(解压出的目录即市场根)add;安装/升级命令见下一节《消费者安装与升级》(用 `claude` CLI,**不是** `/plugin install` 等斜杠命令)。

### 消费者安装与升级(`claude` CLI,在终端跑)

> 命名对照:市场名 `tia-plugins`(`marketplace.json` 的 `name`)、插件名 `tia-mcp`(`plugin.json` 的 `name`)。下面的命令都是 **`claude plugin ...` 终端命令**,不是 REPL 里的斜杠命令——Claude Code **没有** `/plugin install`、`/plugin update` 斜杠命令(REPL 里可用 `/plugin` 打开交互菜单点选)。`claude plugin install/update` 完成后**需要重启 Claude Code** 才生效(官方帮助:`restart required to apply`)。

**首次安装**(把 `dist/tia-plugins-<v>.zip` 解压,或把市场目录放到一个**版本无关**的稳定路径,如 `D:\Tools\tia-plugins`):

```powershell
claude plugin marketplace add <市场目录>
claude plugin install tia-mcp@tia-plugins            # scope 默认 user;装项目级加 -s project
# 重启 Claude Code
```

**升级(覆盖旧版)**——市场目录路径保持版本无关(不要带 `-1.0.2` 这类后缀),用新 build 覆盖其内容后:

```powershell
claude plugin marketplace update tia-plugins         # 重扫市场目录,发现新版本
claude plugin update tia-mcp@tia-plugins             # 把新版拷进已装缓存
# 重启 Claude Code
```

要点 / 排错:

- `plugin update` 报 `Plugin "tia-mcp" is not installed at scope user`?说明装在别的 scope。用 `claude plugin list --json` 查 `scope`,再加 `-s <project|local|managed>` 重跑(本项目实测装在 `project` scope)。
- 升级是否生效靠 `plugin.json` 的 `version` 字段比对——所以发新版**必须**先在源 `plugins/plc-siemens/.claude-plugin/plugin.json` 升版本号(已纳入 `build-plugin.ps1` 流程),否则 `plugin update` 会误判"已是最新"而不动。
- `claude plugin marketplace update <name>` 只刷新市场清单,**不会**动已装插件;`claude plugin update <plugin>@<market>` 才真正覆盖。两步都要。
- 想免手动:REPL 里 `/plugin` → Marketplaces → 该市场 → Enable auto-update(本地/第三方市场默认关)。
- **dev 模式(从源码跑,非 marketplace 安装)**:没有"已安装插件"记录,升级纯源码操作——`git pull` + `dotnet build brands\tia\mcp\TiaMcp.slnx`(改了 worker 再单独 build `...TiaMcp.Openness.Worker.csproj`)+ 重启;无需 reinstall。
- ⚠️ **别直接 marketplace add 仓库里的 `plugins/plc-siemens/`**:dev 版 `.mcp.json` 用 `${CLAUDE_PLUGIN_ROOT}/../../brands/tia/...` 爬层,**只在仓库内 in-place 加载有效**,复制到 marketplace 缓存后路径会断。marketplace 必须用 `build-plugin.ps1` 产出的自包含版。
- 消费者机器前置:TIA Portal V21 + Windows 用户在 `Siemens TIA Openness` 本地组(worker 运行时从其本机 TIA 加载 `Siemens.Engineering.*`)。**无需** .NET SDK,**也无需**自 build worker——worker 已随插件打包进 `server/openness-worker/`,且会在消费者机上从注册表自动校正 codeBase(所以一个包跑在任何 V21 机器上)。

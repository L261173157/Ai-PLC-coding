# P3 —— 真 Openness 后端(TIA V21):已验证的 API 与架构

`TiaMcp.Openness.Worker/OpennessEngine.cs`(net48 worker;从早期进程内 `OpennessBackend.cs`
移植)里的 Siemens.Engineering API 是**一手来源验证**(2026-06)过的——对照 Siemens 自家 XML
文档与官方 `siemens/tia-portal-openness-code-snippets` 仓库。本文件记录关键事实与一个重大架构勘误。

## ⚠️ V21 勘误:net48 模块化,不是 net6 进程内

早期假设("V21 Openness 是 net6,net10 宿主进程内加载——无需 4.8 helper")**错了**。已验证事实:

- TIA V21 把 Openness 拆成**模块化 .NET Framework 4.8** 程序集:
  - `PublicAPI\V21\net48\Siemens.Engineering.Base.dll` —— 核心:`TiaPortal`、`Project`、枚举
    (`TiaPortalMode`、`ExportOptions`、`ImportOptions`、`CompilerResultState`)、`MultilingualText`、
    `Compiler*`。
  - `PublicAPI\V21\net48\Siemens.Engineering.Step7.dll` —— PLC/Step7:`PlcBlock`、`PlcTag`、
    `ProgrammingLanguage`、`PlcBlockGroup`。
  - (V18–V20 是单个 `Siemens.Engineering.dll`;V21 把它拆了——针对 V18 编译的代码在 V21 下**不**
    重新编译就加载不了。)
- 这些是 **net48**,所以 **net10 进程不能进程内承载**。真后端必须跑在 **net48 worker 进程**里;
  net10 MCP server 经本地 IPC 桥到它。

### 真实拓扑(2026-06-17 实现)

```
Agent ──MCP(stdio/http)──> TiaMcp.Server (net10) ──BridgeBackend──stdin/stdout JSON-RPC──> TiaMcp.Openness.Worker (net48)
                                          (Fake in-process)                                      │
                                                                                                 ▼ references Siemens.Engineering.{Base,Step7} (GAC)
```

- `TiaMcp.Openness.Worker` —— **net48** 控制台工程;引用两个 net48 Siemens DLL;在 `OpennessEngine`
  (从旧 `OpennessBackend.cs` 真路径移植)里持已验证的 Siemens 调用 + tags(TagTableGroup);经
  stdin/stdout 逐行 JSON-RPC 循环(`Program.cs`)服务操作。一次读一行请求 → 调用天然串行(一个 TiaPortal)。
- `TiaMcp.Openness`(net10)—— 现在是 **`BridgeBackend : ITiaBackend`**,spawn worker 并经
  `WorkerChannel` 转发每个调用,由 `SemaphoreSlim(1,1)` 串行化。`--backend openness` 时 server 用桥。
  net10 内**无 Siemens 引用**。
- 线协议 + 参数/结果 record 在 `TiaMcp.Contract/Rpc/RpcMessages.cs`;参数/结果以原始 JSON 字形流转,
  使 Contract 不依赖 System.Text.Json 类型。两边用相同选项(`JsonStringEnumConverter`)。
- `Contract` 多目标 `net10.0;netstandard2.0`(一个 `IsExternalInit` polyfill + 用 `Substring` 而非
  ranges 保持解析器 netstandard2.0 干净),故 worker 与 server 共享完全相同的 DTO。

这就是最初的 ".NET Framework 4.8 helper 进程" 逃生口——现确认为 V21 必需,已建。worker **不**在
`TiaMcp.slnx` 里(它需要 net48 dev pack + Siemens DLL);在 TIA 机器上显式构建:
`dotnet build src/TiaMcp.Openness.Worker/TiaMcp.Openness.Worker.csproj`。

### 一次性运行时前置(在 `D:\Program Files\Siemens\Automation\Portal V21` 安装上验证)

1. **`Siemens TIA Openness` 本地组** —— Windows 用户**必须**是成员,否则 `TiaPortal.Open()` 抛
   `COMException`。(已验证:组存在,但 dev 用户需被加入。)
   `Add-LocalGroupMember -Group "Siemens TIA Openness" -Member "<DOMAIN>\<user>"` 后注销/重登。
2. **.NET Framework 4.8 Developer Pack** —— 编译 net48 worker 必需(仅 .NET 10 SDK 无法 target net48)。
   TIA 装的运行时不够。
3. **Siemens DLL 解析(关键)。** 本安装上 Siemens.Engineering.* API DLL **不在 GAC**,且**绝不能**
   拷本地(`Private=True` 会让 `OpennessLocationProvider` 在 `TiaPortal` 握手时抛
   `EngineeringSecurityException` "ensure Copy Local is set to false")。修法:`<Private>False</Private>`
   + worker 的 `App.config` 里一个 `<codeBase>` 提示,把 `Siemens.Engineering.Base`/`.Step7`
   (Version 21.0.0.0,PublicKeyToken 29bfe5fdf4ba5d3b)指向 `PublicAPI\V21\net48`。`codeBase` 在
   **默认加载上下文**加载它们,握手接受;`Assembly.LoadFrom`(load-from 上下文)**不行**——抛同样的
   安全异常。已验证:用 `codeBase`,`new TiaPortal(headless)` 成功。

## 已验证 API 参考(关键调用)

```csharp
using Siemens.Engineering;              // TiaPortal、TiaPortalMode、Project、ExportOptions、
                                        //   ImportOptions、MultilingualText、MultilingualTextItem
using Siemens.Engineering.Compiler;     // ICompilable、CompilerResult、CompilerResultState、CompilerResultMessage
using Siemens.Engineering.HW;           // Device、DeviceItem、Project
using Siemens.Engineering.HW.Features;  // SoftwareContainer
using Siemens.Engineering.SW;           // PlcSoftware
using Siemens.Engineering.SW.Blocks;    // PlcBlock、PlcBlockGroup、FB、OB、FC、ProgrammingLanguage
```

**连接** —— **没有** `TiaPortalProcessMode`;构造器单参:
```csharp
var tia = new TiaPortal(TiaPortalMode.WithoutUserInterface);   // headless(无界面)
// 挂到一个正在运行的实例:
var procs = TiaPortal.GetProcesses();
var tia2 = procs.Count > 0 ? procs[0].Attach() : new TiaPortal(TiaPortalMode.WithUserInterface);
```
Windows 用户必须在 **"Siemens TIA Openness"** 组,否则 `Open()`/`Attach()` 抛 `COMException`。

**打开项目** —— `.ap1x` **文件**(非目录)的 `FileInfo`:
```csharp
Project project = tia.Projects.Open(new FileInfo(@"D:\Proj\Demo.ap21"));
// 归档(.zap21):tia.Projects.Rettrieve(new FileInfo(x), new DirectoryInfo(outDir));
```

**PLC 软件** —— 无 `IPlcSoftware` 接口;具体 `PlcSoftware` 经 `SoftwareContainer`:
```csharp
PlcSoftware? GetPlcSoftware(Device d) {
    foreach (DeviceItem it in d.DeviceItems) {
        var sc = it.GetService<SoftwareContainer>();
        if (sc?.Software is PlcSoftware sw) return sw;
    }
    return null;
}
```

**块** —— `sw.BlockGroup.Blocks`(`PlcBlockComposition`);`sw.BlockGroup.Groups` 为文件夹:
```csharp
PlcBlock b = sw.BlockGroup.Blocks.Find("FB_Motor");   // 找不到返回 null
b.Name; b.Number;              // uint
b.ProgrammingLanguage;         // 枚举:SCL、LAD、FBD、GRAPH(大写)、DB、…
b.Comment;                     // MultilingualText(可空)
// 类型:b is FB / is OB / is FC ;数据块 => b.ProgrammingLanguage == ProgrammingLanguage.DB
// UDT 不在这里:sw.TypeGroup.Types(PlcType)
```

**注释**(已验证——无 `GetText`):
```csharp
foreach (MultilingualTextItem it in b.Comment!.Items) { var text = it.Text; /* it.Language.Culture */ }
```

**导出/导入**(SimaticML XML;可往返):
```csharp
b.Export(new FileInfo(path), ExportOptions.WithDefaults);          // ExportOptions:None | WithDefaults | WithReadOnly
sw.BlockGroup.Blocks.Import(new FileInfo(path), ImportOptions.Override);  // ImportOptions:None|Override|SkipInactiveCultures|ActivateInactiveCultures
b.Delete();   // 无参;对 PlcTag/PlcTagTable/PlcType/Device 同样适用
```
SCL **文本**导出(单向)另走:`sw.ExternalSourceGroup.GenerateSource(new[]{b}, file, GenerateOptions.None)`。

**编译** —— 经块组的 `ICompilable`;结果有 `.Messages`/`.State`(**没有** `.Diagnostics`):
```csharp
var compiler = sw.BlockGroup.GetService<ICompilable>()!;
var result = compiler.Compile();
result.State;          // CompilerResultState:Success | Information | Warning | Error(无 PartialSuccess)
result.ErrorCount; result.WarningCount;
foreach (CompilerResultMessage m in result.Messages) { m.State; m.Description; m.Path; /* 递归 m.Messages */ }
```

**Tags**(都在 `Siemens.Engineering.SW.Tags`;`sw.TagTableGroup` → `.TagTables`/`.Groups`):
```csharp
PlcTagTable table = sw.TagTableGroup.TagTables.Create("MyTable");
PlcTag tag = table.Tags.Create("Motor1", "Bool", "%I0.0");   // (名, 数据类型名, 地址);空地址 => 自动
tag.LogicalAddress; tag.DataTypeName; tag.Comment;            // 不是 Address / DataType
```

**线程** —— Openness 对象非线程安全;串行化所有对一个 `TiaPortal` 的访问(锁或单一专用线程)。无确认的
STA 要求;规则就是"串行"。

**硬件:设备、子网、IO 系统**(`Siemens.Engineering.HW` + `.HW.Features`)—— V21 对照
`siemens/tia-portal-openness-code-snippets` → `NetworkSnippets.cs` 验证,并在 project2/PLC_1 上实跑验证
(2026-06-22):
```csharp
// 整个设备(CPU 站)——已验证,在用:
Device dev = project.Devices.CreateWithItem("OrderNumber:6ES7 515-2AN03-0AB0/V4.1", "PLC_1", "PLC_1");

// 子网——一次调用里创建并连接(原先缺的那块;全新项目上 connect-only 会被静默跳过)。
// 节点来自 NetworkInterface.Nodes.First():
Subnet subnet = node.ConnectedSubnet ?? node.CreateAndConnectToSubnet("PN/IE_1");
node.ConnectToSubnet(existingSubnet);                 // 把第 2 个节点挂到已有子网

// IO 系统——让 PLC 的 PROFINET 接口当 IO controller。需先把节点挂到子网:
IoController ioc = ni.IoControllers.First();          // NetworkInterface.IoControllers
IoSystem io = ioc.IoSystem ?? ioc.CreateIoSystem("PROFINET-IO-System");
```

**模块插入 —— `HardwareObject.PlugNew`**(V21 对照自带的 `Siemens.Engineering.Base.xml` + Siemens
code-snippets `HardwareSnippets.cs` 验证)。`Device` 与 `DeviceItem` 都继承 `HardwareObject`,后者暴露插入面:
```csharp
DeviceItem mod = rack.PlugNew(typeIdentifier, name, positionNumber);  // 创建 + 插入
bool ok = rack.CanPlugNew(typeIdentifier, name, positionNumber);      // 预检
IList<PlugLocation> free = rack.GetPlugLocations();                   // 空槽信息
```
- `positionNumber = 65535` ⇒ **自动挑下一个空槽**(snippet 的惯用法;比自己算更稳)。传显式槽位则强制。
- 插入**机架/导轨**设备项——匹配其 `TypeIdentifier` 含 `System:Rack`(按 snippet),或一个 `Rail`
  命名的顶层项。
- 早期"V21 移除了插入"的假设**错了**——它来自搜 V15–V19 的名字 `DeviceItem.PlugIn(Submodule.PlugInProperties)`。
  该签名没了,但 `PlugNew`(在 `HardwareObject` 基类上)是 V21/V20 的替代。`tia_module_add` 用它,先
  用 `CanPlugNew` 预检以在坏槽/坏类型时给出干净原因。

## 仍待办(P4-real)

- **net48 worker + 桥** ✅ 完成(2026-06-17 实现;首个真 TIA 构建待 net48 dev pack)。
- **Tags**(`ListTags/CreateTag/DeleteTag`)✅ 在 worker 里完成(`TagTableGroup.TagTables` / `Tags.Create`)。
- **网络:子网 + IO 系统** ✅ 完成 + 实跑验证(`CreateAndConnectToSubnet` / `IoController.CreateIoSystem`);
  `tia_network_configure` 现在**创建**它们,不只是连接。
- **SCL 源码导入**经 `ExternalSourceGroup` ✅ 完成(`GenerateBlocksFromSourceAsync` + `tia_block_generate_from_source`;`.awl` 经 sourceName 扩展名)。
  另:结构化 LAD/GRAPH 读写(`tia_block_read_code` / `tia_block_write_code`)的解析/生成全在 net10 侧(`TiaMcp.SimaticML` 库),复用既有 ReadBlockSource / ImportBlock RPC——**worker 零改动**。
  ⚠️ 踩过:worker 的 `OpennessEngine` **也实现 ITiaBackend**——往 ITiaBackend 加 net10-only 方法会直接破坏 worker 构建(slnx 不含它,CI 看不见)。此类方法走独立接缝 `ITiaCodeBackend`(Contract 里,仅 Fake/Bridge 实现)。
- **GRAPH SimaticML 从零生成已真机打通**(2026-08,V21/S7-1511,McpTest 项目):错误信息即向导的迭代实录——`MemoryLayout` 须 `ReadOnly="true"`/Standard;体在 NetworkSource 下 `Graph/v6` 命名空间(内嵌 FlgNet 同属该空间);每步必备 Supervisions(SvCoil)/Interlocks(IlCoil);转移条件须 TrCoil;序列须 EndConnection 终结;步-转移成对编号(1,21,32…);动作操作数=Token(局部带#);接口需 OFF_SQ/INIT_SQ/ACK_EF。空子网/裸触点/no-op 线均被拒;普通 Coil 可导入但编译报"not allowed"。权威样板 `tests/fixtures/FB_GraphDemo.xml`(真机导出 55KB,TIA 自动补全全部运行时接口)。
- **信号模块插入** ✅ 经 `HardwareObject.PlugNew` 完成(`tia_module_add`);`CanPlugNew` 预检 +
  `positionNumber 65535` 自动槽。
- **PROFINET 设备名** —— `set_PnDeviceName` 不是 `Node` 属性;正确目标仍未验证,故 `pnDeviceName` 被跳过。
  已知限制,不在关键路径上。
- **硬件读保真度** —— `ReadHardwareConfigAsync` 列子网,但上报其 `nodes`/`ioSystems` 为空(它在子网上找
  IO 系统;而 IO 系统挂在 IoController 上)。仅显示层问题。
- **CPU 系统/时钟内存读在 S7-1200 上不可用** —— `SystemMemoryByte`/`ClockMemoryByte`(及其 `*Address`)
  这几个 `GetAttribute` 名在 **S7-1500** CPU 上正常返回 bool/地址;在 **S7-1200** CPU 上 `GetAttribute`
  直接抛 `EngineeringNotSupportedException`(2026-07-31 在 CPU 1215C 实测)。这是 Siemens 侧的 API 差异,
  非改名可绕过。`ConfigureCpuMemoryAsync` 检测到该异常后**明确报出**(而不是悄悄返回 false/0,那会误以为
  该功能关闭——而项目里 `%MB0/%MB1` 其实是配了的)。`FindCpuItem` 因此优先按属性找 CPU(S7-1500 快路径),
  回退到"持 PlcSoftware 的 item"(S7-1200),再由调用方判定是否支持。
- **在线/下载**真(`IPlcWebApi`)、**每会话 AsyncLock**(当前 worker 单线程故已串行;AsyncLock 只在 worker
  将来并发时才有意义)。
- **⚠️ 编译可能整垮整个 Portal(attach 模式)。** 2026-06-23 实测:在*已挂接*的 GUI Portal 上跑
  `tia_project_compile`(Software,设备作用域)抛 `Siemens.Engineering.NonRecoverableException`
  ("no exception message available")并**拖垮整个 TIA Portal 应用**(事后 0 个 `Siemens.Automation.Portal`
  进程)。其后每次调用都以 `EngineeringObjectDisposedException` 失败。该编译在失败前跑了 **167 秒**
  (记在 Claude 的 mcp-logs-tia jsonl 里——只有协议计时留存;worker stderr 转发到 server stderr 且**未持久化**,
  故崩溃时的 `[tia] notification:` 行丢失了)。
  **根因排查(2026-06-23,无实机复现——TIA 已挂):**
  - `Compile()` 抛 `NonRecoverableException` 是**已知的通用 Openness 灾难性故障**,非 attach 专属:成熟的
    多版本参考适配器 `tiankongduidui/TiaPortalOpennessDemo`(V15–V20)把每个 `compiler.Compile()` 都显式
    `catch (NonRecoverableException)`。所以该异常类型本身是预期/可辩护的——我们的 `RunCompile` 镜像就是标准处理。
  - 同一参考只在 **import/write** 操作周围用 `TiaPortal.ExclusiveAccess(...)`,**编译周围不用**——故
    "编译周围缺 ExclusiveAccess" 不太可能是触发点。
  - attach 模式**放大后果**(共享 GUI Portal 随 API 进程而死;网络资料确认 TIA 崩溃会拖垮整套 + 未存工作),
    但不像是触发点。
  - 我们配置后 `hardware_read` 里空的 `nodes`/`ioSystems` 是**有文档的读侧限制**(读 `Subnet.IoSystems`;
    IO 系统挂在 `IoController` 上),**不是**工程畸形的证据——故"半建的 IO 系统"比最初想的弱。
  - **2026-06-23 尝试复现(实机,TIA 已重开)——每个具体假设都被推翻:**
    (A) 干净已存项目,attach,Software 编译 → ✅ 成功,**14 秒**,无崩溃,0 通知。
    (B) 重新经 `network_configure` 建相同子网/IO,不存,编译 → ✅ 成功,**0.7 秒**。
    (C) 完整重放崩溃那次的软件序列(`block_read_source` + `tag_create` + `tag_delete`)再编译 → ✅ 成功,
    **0.7 秒**。(D) 用户确认原始 167 秒编译期间**无模态/UI 确认**——"它自己卡住的"——故 UI 交互阻塞也排除。
    用户看到的 Openness 访问确认对话框只在首次 attach 时出现(`Connect` 有一次约 85 秒),不是每次调用,
    且无关。
  - **结论:一个已劣化的长寿命 attach 会话里的非确定性灾难性故障。** 决定性证据是时序而非序列:崩溃那次的
    操作异常慢(`block_read_source` 14 秒——它走了 export-fail → reconcile-`Compile()` 重试路径;致命编译
    167 秒),而完全相同的操作在健康会话里跑亚秒到 14 秒。故障前 Portal 已在劣化。这正是成熟适配器防御性
    `catch (NonRecoverableException)` 的原因——它不可预测、客户端无法阻止;优雅处理 + 恢复是唯一正确响应,
    现已就位(`RunCompile`/`PortalCrashed`/`IsPortalAlive`/worker respawn)。worker stderr 现自动持久化
    (`Program.SetupStderrLog` → `%TEMP%\tiamcp-worker-logs\worker-<ts>-<pid>.log`),若复发崩溃时的 `[tia]`
    事件会被捕获。运维缓解:重操作优先用 **spawned** 实例,长会话里**定期重启被挂接的 Portal** 以避开劣化态。
  要点:"attach 以避开锁麻烦"是双刃剑——Openness 操作崩溃会带走用户共享的 GUI Portal;重/险操作(编译/下载)
  在 spawned 实例上更安全。✅ 2026-06-23 已处理:`RunCompile()` + `PortalCrashed()` 包住 `compiler.Compile()`
  (含 `CompileAsync` 路径与 `ReadBlockSourceAsync` 的 reconcile 重试)及 `provider.Download()`;
  `NonRecoverableException`/`EngineeringObjectDisposedException` 现会丢弃死句柄(`DropDeadPortal`)并抛
  "TIA Portal entered an unrecoverable state during {op} … reopen + reconnect",而非下次调用时一个裸的
  disposed-object。崩溃本身仍会发生(根因未修)——这里只是让它可恢复 + 可读。
- **陈旧 Portal 误报** ✅ 2026-06-23 已修。上述崩溃后,`tia_status` 仍报 `tiaAvailable:true`,`tia_connect
  mode=attach` "成功"(返回缓存的项目列表),尽管 `_portal` 已 dispose——两者都判 `_portal is not null`,
  而死句柄上它仍非 null。加了 `OpennessEngine.IsPortalAlive()`:探测句柄(往返 `_portal.Projects`),在
  `EngineeringObjectDisposedException`/`NonRecoverableException`/`COMException` 时丢弃陈旧引用 + 缓存项目。
  `GetStatusAsync` 与 `EnsurePortal` 现都用它,故状态如实上报、attach 真正重新挂接。已验证:构建干净;无 portal
  时 attach 现在有用地报错而非误报成功。会话中途死亡那条路径本身没端到端再验证(需要有活 Portal 再崩一次)。

## 来源

一手(Siemens 自带程序集 XML 文档):`github.com/Parozzz/TiaUtilities/SiemensAPI/V21/`
(`Siemens.Engineering.Base.xml`、`Siemens.Engineering.Step7.xml`)。Siemens 官方仓库:
`github.com/siemens/tia-portal-openness-code-snippets`(V21,net48 模块化引用)。交叉核对的工作代码库:
`dotnetprojects/DotNetSiemensPLCToolBoxLibrary`(V15_1/V19/V21)、`VValter-Bach/TIA-writer`、
`cezar1/TiaExportBlocks`。Siemens 文档门户:
`docs.tia.siemens.cloud/.../v21/readme-tia-portal-openness/major-changes-...`。

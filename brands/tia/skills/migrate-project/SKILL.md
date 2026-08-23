---
name: migrate-project
description: "把一个现成参考项目里的 UDT / FB / FC 批量搬到另一个项目(如新建项目),复用它的类型层 + 库块。"
---

# 跨项目搬运 UDT 与块

> ℹ️ **本篇是"跨项目搬运"**——在**同一版本(V21)**的两个项目之间复制 UDT/块;**不是** TIA 的"版本升级/迁移"。把旧版本(V18/V19)项目升到 V21 走 TIA 自带的 *Upgrade & Migrate*:用 V21 打开旧 `.ap18/.ap19` → 升级重组 → 另存为 `.ap21`,本 skill 不涉及。

## 场景与心智模型

- **源项目**有现成、跑通过的 UDT/块;**目标项目**要复用。
- 搬运 = **导出(源)→ 改造 → 导入(目标)**。两端都是 SimaticML XML,但目标项目环境(注释语言、已有类型)不同,要处理三个坑(见下)。
- 典型场景:从一个跑通过的参考项目(类型层 + 库块齐全)搬到新建/目标项目。

## 前置条件

- 源、目标项目在同一台 TIA V21 机器上。**一个会话同时只能开一个项目**,搬运要在源/目标之间切换(`tia_project_close` + `tia_project_open`),或先在源端把全部 XML 导出到磁盘,再切到目标端一次性导入(推荐,少切换)。
- `--mode ReadWrite`。
- worker 需是支持 UDT 导出 / 文件路径导入 / typegroup+blockgroup 目标 / UDT 删除的版本(见下)。旧版 worker 搬不了 UDT。

## 依赖的工具能力

| 工具 | 现在能做什么 |
|------|-------------|
| `tia_block_export(path, format="Xml", outDir)` | 导出 block **或 UDT**(找不到 block 时回退 PlcType),每个 → `<Name>.xml`。UDT 不能 SclSource。 |
| `tia_tagtable_export(path, outDir)` | 导出**整张 tag 表**(含其 tags)为 SimaticML XML → `<TableName>.xml`,是 `tia_tagtable_import` 的逆运算,补 tag 层源(子组里的表按名查得到)。 |
| `tia_udt_import(plcPath, sourceXml)` | `sourceXml` 可传**文件绝对路径**(不必内联 XML);`plcPath` 带 `/typegroup:NAME` → 导入到指定子组(不指定→根);导入子组 = **移动**(先删同名,再建),适合重新归档。 |
| `tia_block_import(plcPath, name, source)` | `source` 可传文件路径;`plcPath` 带 `/blockgroup:NAME` → 导入到指定子组。 |
| `tia_block_delete(path, confirm=true)` | 现在能删 **UDT**(回退 PlcType),不止 block。 |
| `tia_cpu_system_clock_memory(devicePath, enableSystemMemory?, systemMemoryByte?, enableClockMemory?, clockMemoryByte?)` | 配 CPU 系统/时钟存储字节。省略参数=读当前;给值=写。启用后 **TIA 自动建系统/时钟 tag**(FirstScan/AlwaysTRUE/Clock_*),G_* 库块才能编译。见工作流 C。 |

## 工作流 A:搬运 UDT

1. **导出**(在源项目):对每个 UDT `tia_block_export(format="Xml", outDir=<src目录>)` → `<Name>.xml`。
2. **拓扑合并**:把一批 UDT 合进一个多类型文件,且**依赖在前**。
   - ⚠️ Siemens 导入**拒绝前向引用**:`ParentType.ChildField` 引用 `ChildType`,则 `ChildType` 必须在文件里排在前面。多类型文件必须拓扑序。
   - 用下面的 `merge.ps1`(自动分析每个类型的 `"引用类型"`、拓扑排序、剥 ObjectList+成员 BooleanAttribute、重编 ID)→ `_merged_<批>.xml`。
3. **导入**(在目标项目):`tia_udt_import(plcPath + "/typegroup:<组>", "_merged_<批>.xml 路径")`。
   - **跨组依赖不用担心**:类型全工程按名解析,还没搬的留在根,搬运过程中引用始终能解析。
   - 导入子组会返回 `Imported 0 UDT(s)` —— 正常,因为删了同名又建,全工程名集合没变(diff=0)。**移动确实发生了**,看 `tia_udt_list` 的 `groupPath` 确认。

## 工作流 B:搬运块(FB/FC/DB)

1. **导出**(在源项目):对每个块 `tia_block_export(format="Xml", outDir=<src目录>)` → `<Name>.xml`。
2. **剥文化**:用下面的 `strip-cultures.ps1` 去掉所有 `<MultilingualTextItem>`(块注释/标题)→ `<Name>.zhcn.xml`。
   - ⚠️ 新建项目(经 `tia_project_create`)没有源项目的注释语言(en-GB/en-US/zh-CN),块导入会报 `Cannot import multilingual text with culture 'X' ... does not exist within the current project`。严格的形态是 ObjectList 里的 `<MultilingualTextItem>`(块注释/标题);成员注释(Interface 里的 `<MultiLanguageText Lang=>`)宽松,不受影响(这也是为什么 UDT 搬运没踩这个坑——合并时已剥了 ObjectList)。
   - 根治办法:把目标项目的注释语言设成和源一致(暂无工具,需 GUI 或加工具);剥文化是 workaround,会丢块注释/标题文本(逻辑和成员注释保留)。
3. **导入**(在目标项目):`tia_block_import(plcPath + "/blockgroup:<组>", name, "<Name>.zhcn.xml 路径")`。
   - ⚠️ **调用顺序**:被调块必须先在(把被调 FB 当实例变量也算"类型引用",如报 `Data type "<DependedBlock>" is unknown`)。**多趟**:先全导一遍,失败的重试(被调块在前趟已建)。
   - ⚠️ **可复用库块常引用整套类型系统**(不止基础类型那一批):设备块可能依赖各类设备 UDT(如某视觉/伺服/PC 通信块 → 对应的 UDT)。**搬块前先把 UDT 层补全**,否则设备块导入失败。

## 工作流 C:系统层(让库块编译 0 错)

G_* 库块的网络里引用 `AlwaysTRUE`/`AlwaysFALSE`/`FirstScan`/`Clock_10Hz`/`Clock_2Hz` 等,它们都是 CPU「系统与时钟存储字节」的位 —— 目标项目默认没启用这俩字节,库块编译会报一堆 `Tag "AlwaysTRUE" not defined`(全是这 5 个系统 tag)。

- **系统字节**(默认 MB1):bit0=FirstScan、bit1=DiagStatusUpdate、bit2=AlwaysTRUE、bit3=AlwaysFALSE
- **时钟字节**(默认 MB0):bit0..7 = 10/5/2.5/2/1.25/1/0.625/0.5 Hz

修法:
1. 先查源项目的字节地址(`tia_tag_list`:`FirstScan` 在 `%M1.0` → 系统字节 MB1;`Clock_10Hz` 在 `%M0.0` → 时钟字节 MB0)。
2. `tia_cpu_system_clock_memory(devicePath, enableSystemMemory=true, systemMemoryByte=1, enableClockMemory=true, clockMemoryByte=0)` —— 配好后 **TIA 自动建出 14 个系统/时钟 tag**(System_Byte/FirstScan/AlwaysTRUE/.../Clock_Byte/Clock_10Hz/...),**不用手动建 tag**(手动建会报 "already exists")。
3. 再 `tia_project_compile` → 0 错。

## 工作流 D:完整框架(含工位层 + tags + 硬件)→ 用克隆,不要逐块搬

工作流 A/B 只搬得动 **UDT + 块**。一旦目标要带上 **工位层**(它的块符号化引用 IO tag)、**tag 表**、**硬件配置**(PROFINET 从站/GSD),逐块迁移路线**走不通**——MCP **没有 tag 表/硬件导出工具**(`TagTools` 只有 list/create/delete/import;硬件无导出、GSD 从站无法经 MCP 安装),工位块编译必报 `Tag "..." not defined`。要拿一个**完整且能编译**的框架,只能**整项目克隆**:

```
tia_connect mode=headless
tia_project_open  <源 .ap21>  visible=false          # 如 <your-ref-project>/<name>.ap21
tia_project_save_as  projectPath=<源>  targetDirectory=<父目录>  targetName=<新名>  rebind=false
# → 在 <父目录>/<新名>/ 生成完整克隆(块/UDT/tag/硬件/GRAPH/库引用全带走)。
```

然后**显式**打开克隆(不要靠 rebind):

```
tia_project_close  projectPath=<源>  saveBeforeClose=false
tia_project_open   <父目录>/<新名>/<新名>.ap21  visible=false   # 克隆按新名寻址
tia_project_compile scopePath=<device> mode=Software            # headless!attach 会崩 GUI
```

克隆后想给源池补工位层的 VCS 友好源文件(可选,导成 .xml 纳入版本管理):

```
tia_block_list     path=<device>/plc:program          # 挑工位块 / DB / OB / GRAPH
tia_block_export   path=<device>/plc:program/block:<StationBlock>  format=Xml  outDir=plc/.block_src
tia_tagtable_export path=<device>/plc:program/tagtable:<Table>  outDir=plc/.tag_src   # 补 tag 层
```

⚠️ **save_as 的坑:**
- **`rebind=true` 会留 disposed 旧句柄**:save_as 内部 Close 源 + 立即 Open 同名克隆,worker 缓存的 Project 引用变成 disposed → `tia_project_list` 抛 `EngineeringObjectDisposedException`,`tia_project_open` 报 "another project already open"。**修法**:用 `rebind=false` + 显式 close+open(上面那样);万一已经踩了,`tia_disconnect`(新工具,见下)释放整个 Portal 再重连即可,不必 kill 进程。代码层已加防御(`ResolveProject` 跳过 disposed 句柄),但 rebind=false 路线最干净。
- **克隆按新名寻址**:save_as 后克隆用 `project:<新名>` 寻址(实测内部名会跟文件夹名),**不是**源名。
- **`tia_status` 的 `tiaAvailable`**: = TIA V21 是否**装了**(注册表探测),不随"有没有开 session"变;别据此判断"没装 TIA"。

### 释放 headless 实例

搬完/编完想释放 headless TIA 的 ~2GB 内存,不用再 OS 级 kill 进程:

```
tia_disconnect        # 关项目 + Dispose Portal,worker 留着,下次 tia_connect 重起一个新 Portal
```

### Openness 暂不支持、只能克隆绕过的三件事

| 想要 | 现状 | 办法 |
|------|------|------|
| **强制全量重编译**(确认 0 错非缓存) | Openness `ICompilable.Compile()` 只增量,无 rebuild-all 入参 | 增量编译的链接/一致性检查每次都跑,"0 错"就是权威;要绝对确证就 close+open 再编一遍 |
| **项目改名** | `Project.Name` 只读;SaveAs 不保证改名 | 克隆到目标名目录,重开即按新名寻址;要内部名严格同步只能在 GUI 改 |
| **硬件配置导出**(PROFINET/GSD) | 无导出 API、GSD 无法经 MCP 装 | 整项目克隆(工作流 D) |

## 配套脚本

两个脚本只依赖 PowerShell 内置 XML,放到任意工作目录即可。**真要用时再读源码**:

- [`scripts/merge.ps1`](scripts/merge.ps1) —— UDT 拓扑合并:`merge.ps1 -Dir <UDT xml 目录> -Out <批名> -Names "TypeA,TypeB,TypeC"` → `_merged_<批>.xml`(依赖在前,最小化:剥 ObjectList + 成员 BooleanAttribute,重编 ID)。
- [`scripts/strip-cultures.ps1`](scripts/strip-cultures.ps1) —— 剥块多语言注释:`strip-cultures.ps1 -In <块.xml> -Out <块.zhcn.xml>`(去掉 ObjectList 的 `<MultilingualTextItem>`,目标项目缺这些 culture 时用)。
- 批量剥:`Get-ChildItem <Dir> -Filter *.xml | ? { $_.Name -notlike '*.zhcn.xml' } | % { & scripts/strip-cultures.ps1 -In $_.FullName -Out (Join-Path $Dir ($_.BaseName+'.zhcn.xml')) }

## 常见报错 → 修法

| 报错 | 原因 / 修法 |
|------|-------------|
| `Data type "X" is unknown`(导入时) | 缺被引用的类型/块;先导它(UDT 用拓扑序 `merge.ps1`,块用多趟重试) |
| `culture 'en-GB' ... does not exist within the current project` | 目标项目缺注释语言;`strip-cultures.ps1` 剥文化,或把项目注释语言设成和源一致 |
| `block name 'X' ... already exists in this CPU` | 同名已在别处;UDT 导入子组会先删(move 语义);块目前不删,先 `tia_block_delete` |
| `Inconsistent blocks and PLC data types (UDT) cannot be exported` | 对象刚导入未编译;导出前先 `tia_project_compile` 让它一致 |
| `Tag "AlwaysTRUE"/"FirstScan"/"Clock_*" not defined`(**编译时**) | 目标项目没启用 CPU 系统/时钟存储字节;`tia_cpu_system_clock_memory` 配 MB1+MB0,TIA 自动建出这些 tag(见工作流 C) |
| 导入子组返回 `Imported 0` | 正常——删同名又建,全工程 diff=0;看 `groupPath` 确认移动 |
| worker 构建报 DLL 被锁 | 跑着的 worker 锁了输出;`Stop-Process -Name TiaMcp.Openness.Worker` 再 build,server 自动重启新 worker |

## 校验

- `tia_udt_list` / `tia_block_list` 看 `groupPath`,确认进对了子组、根下没残留。
- 库块搬完后,先做**工作流 C(系统层)**,否则编译报 `Tag "AlwaysTRUE"... not defined`。
- `tia_project_compile`(**`attach` 会崩整个 TIA GUI**,用 **headless** 实例:`tia_connect mode=headless` → open → compile)直到 0 错。⚠️ 搬运后项目 inconsistent,需一次编译对齐。

## 待补

- **设项目注释语言**的工具(根治剥文化的 workaround)。
- ~~`ExportBlockAsync` 加编译重试~~ ✅ 已实现(`ExportToFileWithRecovery`:导出失败→对齐编译→重试;read_source 同机制)。
- Openness 无 API 的三件:强制全量重编译、项目改名、硬件配置导出 —— 只能克隆绕过(见工作流 D 表)。

相关:[`写程序`](../write-program/SKILL.md)(单块从零写)、[`复用已有模块`](../reuse-library/SKILL.md)(从 `.al21` 库实例化)、[`SimaticML速查`](../_reference/simaticml-reference.md)、[`从零搭一个站`](../overview/examples/从零搭一个站.md)。

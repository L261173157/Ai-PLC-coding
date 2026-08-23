---
name: write-program
description: "给 PLC 建变量(tags)、数据类型(UDT)、逻辑块(OB/FB/FC,语言 SCL/LAD/GRAPH),并编译到 0 错误。"
---

# 写程序

## 前置条件

- 硬件已配好(见 [`硬件组态`](../hardware-config/SKILL.md));项目里有 PLC。
- `--mode ReadWrite`。

## 第一步永远是「先读懂」

改任何已有逻辑前,先看清楚:

- `tia_block_read_code` —— 读块**本体的紧凑结构化视图**(LAD 网络=布尔表达式+线圈+盒清单;SCL=拍平文本;GRAPH=步/转移视图)。**先试这个**,比生 XML 省 token 得多。
- `tia_block_read_source` —— 读块的 SimaticML XML 源(需要原始 XML/精确改写时)。
- `tia_interface_read` —— 读块/UDT 的**结构化接口成员树**(分段→成员,带数据类型/初值/注释,结构体嵌套),比生 XML 好读。
- `tia_cross_reference` —— 交叉引用:这个块**被谁用 / 用了谁**,带引用类型(Uses/UsedBy)和访问(读/写/调用)。改前评估影响面。
- 枚举:`tia_block_list`(带所在用户组路径)、`tia_udt_list`、`tia_tagtable_list`。

## 代码注释(硬性要求)

写出来的代码必须有清晰中文注释——这是**硬性要求**,不是建议。两条铁律 + 分语言写法(SCL `//`、UDT 成员 `Lang="zh-CN"`、LAD 网络注释)见 [`代码注释规范`](../_reference/code-comments.md)。要点:块 / UDT 顶部写用途,每个管脚 / 成员一句中文注释,关键逻辑行注释意图(别写复述代码的废话)。本篇所有示例都按此规范。

## 建数据对象

- **变量** `tia_tag_create`(单个)或 `tia_tagtable_import`(整表 SimaticML XML)。
- **UDT** `tia_udt_import`(SimaticML XML)。
  - 完整 schema(最小可导入形态 + 6 个手敲坑:成员注释是元素文本而非 `Text=` 属性、`<StartValue>` 不带 `SystemString`、ObjectList 类型注释用 `MultilingualTextItem`、…)见 [`SimaticML速查`](../_reference/simaticml-reference.md) 的「UDT」节。**最省事:导出一个相似 UDT 当模板改**,别从零手敲。
  - `sourceXml` 可传**文件路径**;`plcPath` 带 `/typegroup:NAME` 导入到子组(=移动)。批量搬运/重新归档见 [`跨项目搬运`](../migrate-project/SKILL.md)。

## 写逻辑:三条路径

### 路径 A —— SCL 文本(最省事,首选)

`tia_block_generate_from_source(plcPath, sourceName, sourceText)`:直接喂 **SCL 文本**(`FUNCTION` / `FUNCTION_BLOCK …`),走 ExternalSources 生成真实块(如生成一个带自保持的电机控制块)。新建/简单逻辑用这条。
- **定时器/计数器/状态机也走这条**:IEC 定时器声明为 `TON_TIME`(FB 放 Static),`#t(IN:=…, PT:=T#3S)` 调用、读 `#t.Q`。比手写 LAD 定时器可靠得多 —— 见 [`定时器范例`](examples/定时器范例.md)(已实测 0 错误)。

### 路径 B —— SimaticML(LAD / GRAPH,或需要精确控制结构时)

`tia_block_export`(导出一个相似块当模板)→ PowerShell `[xml]` 改 → `tia_block_import`。
- LAD = FlgNet;GRAPH 用模板法(导出相似 GRAPH 块→改步/转换/动作→重导入,**可往返**)。
- `tia_block_import` 的 `source` 必须是 **SimaticML XML 不是 SCL**;`type`/`language` 参数被忽略(XML 自带)。
- ✅ **同名块会被覆盖**(ImportOptions.Override),幂等——重导入不需要先删。仅当你确实要**移除**块时才用 `tia_block_delete`(破坏性,需 confirm)。
- ⚠️ 手写 XML 时 `<AttributeList>` 必须含 `<Namespace />`,否则报 `Missing 'Namespace'`。
- **手把手从零写一个 LAD 启保停**(完整可复制 XML,已实测):[`启保停LAD范例`](examples/启保停LAD范例.md)。
- 详细 token / FlgNet / StructuredText 配方见 [`SimaticML速查`](../_reference/simaticml-reference.md)。

### 路径 C —— 结构化读写(LAD + GRAPH,免手写 XML)

- **读**:`tia_block_read_code(path)` —— LAD 网络直接回**布尔表达式 + 线圈 + 盒清单**(如 `(Start_PB OR Motor) AND NOT Stop_PB = ( ) Motor`),SCL 块回拍平文本,**GRAPH 块回步/转移/动作结构化视图**(步名/初始步/N-R-S 动作/转移条件/每步监控联锁条件)。纯布尔网络还带 `logic` 表达式树(**与写 spec 同形**,读→改→写回闭环)。带 `networkFrom/networkTo` 翻页,`includeInterface=false` 只看本体。解析不了的网络回退结构化清单,不会给错误表达式。
- **写**:`tia_block_write_code(plcPath, specJson)` —— 结构化 JSON spec 直接生成 SimaticML 并导入(Override 幂等)。
  - **LAD** v1 指令集:触点(NO/NC)、and/or 嵌套、coil/set/reset、TON/TOF/TP(多重实例+PT,FB)、MOVE、内联比较 eq/ne/ge/gt/le/lt。
  - **GRAPH**:线性序列 `sequence: [{name, actions:[{qualifier, operand}], transitionOperand}]`(限定符 N/R/S/D/L/ON/OFF/TD/TF/TL/CD/CR/CS/CU;**已在真机验证导入+编译 0 错**;首个非标准 Input 成员兼作每步必备的监控/联锁条件;复杂拓扑用路径 B 模板法)。
  - `dryRun=true` 只返回生成的 XML 不导入(ReadOnly 也可用)。spec 里的操作数:接口里声明过的按局部变量,否则按全局 tag(先 `tia_tag_create`);数值字面量直接写(`8.0`、`T#3S`)。
- 超出指令集(边沿触点、CTU、Call 带形参、GRAPH 并行/分支等)→ 报错并提示改走路径 B 模板法。

### 路径选择

| 情况 | 用 |
|------|----|
| 新写 SCL 逻辑(FB/FC/FUNCTION) | 路径 A |
| 新写/读懂 LAD 梯形图 | **路径 C**(读写都省 token;超出 v1 指令集再走 B) |
| 新写/读懂 GRAPH 线性顺控 | **路径 C**(超出线性拓扑再走 B) |
| 克隆改造现有块 / 并行分支 GRAPH / 超出 C 指令集 | 路径 B |

## 闭环:编译 → 修 → 重编

`tia_project_compile` → 读**递归诊断**(错误文本在叶子节点,已支持递归收集)→ 改 → 重编,直到 **0 错误**。
⚠️ 逻辑块只有**被循环 OB(Main/OB1)调用**才会运行——新建的 FC/FB 记得在 OB1 里调它(LAD `Call`,见速查)。

## 校验

`tia_project_compile` Software **0 错误**(警告可接受);`tia_block_read_source` 能读回刚写的逻辑。

## 常见报错 → 修法

| 报错 | 原因 / 修法 |
|------|-------------|
| import 报 XML 不合法 | `source` 要 SimaticML XML 非 SCL;先 `[xml]$x=Get-Content` 校验良构 |
| import 报 `Missing 'Namespace'` | 块的 `<AttributeList>` 漏了 `<Namespace />`(FC/FB/OB/UDT 都要) |
| write_code 报"不在 v1 指令集" | 边沿触点/CTU/Call 带形参等暂不支持结构化写入;改走路径 B 模板法 |
| UDT import 报 TargetInvocation / `'X' attribute is not declared` | 多个 schema 坑;完整列表见 SimaticML速查「UDT」节(最稳:导出模板改) |
| 块编译过但"不运行" | 没在循环 OB 里调用它 |
| StructuredText 报 "UId 缺失" | v4 命名空间每个元素都要 UId(含 Symbol/Component),见速查 |
| 创建项目报路径过长 | TIA 项目路径上限 143 字符,换浅一点的目录(如 `C:\TiaTmp`) |
| `tia_catalog_search` 输出爆掉(100k+ 字符) | 查询要给具体订货号/型号(如 `6ES7 511-1AK02`),别用宽泛词 |

## 状态

已实测:block_generate_from_source(SCL FUNCTION_BLOCK 启保持)、udt_import、interface_read、cross_reference、block_list/udt_list/tagtable_list、SimaticML LAD/SCL/GRAPH 往返导入、编译递归诊断、**从零手写 LAD 启保持 FlgNet**(见 [`启保停LAD范例`](examples/启保停LAD范例.md))、**从零手写 LAD 多分支 + TON 定时器**(见 [`定时器范例`](examples/定时器范例.md))—— 均导入 + 编译 0 错误。
已实测(Fake 离线):路径 C 的 `tia_block_read_code`(启保停表达式精确还原)与 `tia_block_write_code`(标准集 spec 写入→回读往返一致、同名幂等、lint 铁律);真机编译验证见 `tests/verify_lad_graph.py`。
TODO:带形参 Call 子块(在 OB1 里 LAD `Call` FB/FC 并传参)的手把手范例;计数器(CTU)、其它复杂指令的手写范例(机制同定时器,Part+Version);write_code 支持边沿触点/网络标题。

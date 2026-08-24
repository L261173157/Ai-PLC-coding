# SimaticML 速查(LAD/SCL/GRAPH 导入导出)

> 何时用:走「写程序」的路径 B(`tia_block_export` → 改 XML → `tia_block_import`)时,查 SimaticML 各结构的写法与坑。

## 总原则

- `tia_block_import` 的 `source` 必须是 **SimaticML XML**(不是 SCL 文本);`type`/`language` 参数被忽略,XML 自带。
- ✅ **同名块会被覆盖**(ImportOptions.Override),幂等——替换已有块直接重导入即可,不需要先删。仅当真要移除块时才 `tia_block_delete`(破坏性,需 confirm)。
- 最可靠的造块法:`tia_block_export` 一个相似块当**模板**,用 PowerShell `[xml]` 改,重导入(旧块自动被覆盖)。**别手敲几 KB XML**——先 `[xml]$x=Get-Content -Raw` 校验良构。
- ⚠️ **块的 `<AttributeList>` 必须含 `<Namespace />` 元素**(FC/FB/OB/UDT 都要),否则导入报 `Missing 'Namespace' identifier attribute`。最小手写版易漏(导出的模板自带,所以模板法不会踩)。
- 元素名带点(`SW.Blocks.FC`),XPath 要用 `local-name()`:`//*[local-name()='SW.Blocks.FC']`。
- TIA 导入时会规范化(重排 UId、补装饰属性如 `HeaderAuthor`/`IsIECCheckEnabled`/`Accessibility`、给符号引用加 `HasQuotes`)——所以 **UId 的具体值无所谓、最小版即可,只看结构与唯一性**;导入后再 export 可看规范化全貌。

> **与路径 C 的分工**:标准集内的 LAD(触点/并串/线圈/置位复位/IEC 定时器/MOVE/比较)直接用
> `tia_block_write_code` 的结构化 spec,不用碰本速查的 XML 细节;`tia_block_read_code` 则把这些
> XML 反向解析成表达式。本速查覆盖超出该指令集、以及 GRAPH/UDT/Call 等仍需手改 XML 的场景。

## SCL 逻辑(StructuredText,在 CompileUnit 里)

SCL 代码不是纯文本,放在:
`<NetworkSource><StructuredText xmlns="…/StructuredText/v4">…</StructuredText></NetworkSource>`(位于 `<SW.Blocks.CompileUnit>` 内),是一串 token 元素。

- token 种类:`<Token Text=":="/>`(还有 `(` `)` `OR` `AND` `NOT` `;` `IF`…)、`<Blank/>`、`<NewLine/>`、`<Access Scope=…>`。
- **⚠️ v4 命名空间比 FlgNet 严:每个元素都要 `UId`**——包括 `Access` 里的 `<Symbol UId>` 和 `<Component Name=… UId>`(漏了报 "The 'UId' attribute is missing in 'Symbol'…")。FlgNet(LAD)则**不要求** Symbol/Component 带 UId,两者不同。
- 例:`Motor := (Start OR Motor) AND NOT Stop;` 的 token 序列 =
  `Access(Motor)` Blank `Token(:=)` Blank `Token(()` `Access(Start)` Blank `Token(OR)` Blank `Access(Motor)` `Token())` Blank `Token(AND)` Blank `Token(NOT)` Blank `Access(Stop)` `Token(;)` NewLine。
- IO 用**符号 tag** 引用(`Scope="GlobalVariable"`),先用 `tia_tag_create` 建好;绝对地址 `Scope="Absolute"` 写法不确定,尽量避开。

> 提示:新写 SCL 优先用「写程序」路径 A(`tia_block_generate_from_source` 喂 SCL 文本),比手造 StructuredText 省事得多。本速查主要用于 LAD/GRAPH。

## LAD 逻辑(FlgNet)

网络在 `<NetworkSource><FlgNet xmlns="…/FlgNet/v5">…</FlgNet></NetworkSource>`,由 `<Parts>` + `<Wires>` 组成。

### ⚠️ 接线四铁律(违反必报错)

1. **一个网络只能有一根母线 wire**:所有接左母线的点(各触点 `in`、功能盒的 `en`)必须并到**同一根** `<Wire><Powerrail/><NameCon …/><NameCon …/></Wire>` 里。多根 `<Powerrail/>` wire → 报 "networks can only contain one power rail"。
2. **一个源 pin 只能出现在一根 wire**:要一对多就在**那一根 wire 里写多个 `<NameCon>`**(如母线接多个触点);同一个 `out` 拆到两根 wire → 报 "out is used multiple times"。
3. **线圈/盒输入要"能流"驱动**:线圈 `in` 必须由触点/逻辑输出(能流)喂入,**不能用功能盒的数据输出(如定时器 `Q`)直驱线圈** → 报 "power rail … invalid connection at pin"。读盒输出要**另起一个触点**读它(见定时器范例)。
4. **带类型字面量用 `Scope="TypedConstant"` 且不带 `<ConstantType>`**(如 Time `T#3S`);`LiteralConstant` 只给类型可推断的裸字面量。

- **常开触点(NO)**:`<Part Name="Contact" UId="X" />`。
- **常闭触点(NC)**:`<Part Name="Contact" UId="X"><Negated Name="operand" /></Part>`。
- **线圈**:`<Part Name="Coil" UId="X" />`。触点/线圈各有 `in`/`out`/`operand` 接点;地址由 `<IdentCon UId=accessId/>` →`<NameCon Name="operand"/>` 喂入。
- **OR(并联分支)**:`<Part Name="O" UId="X"><TemplateValue Name="Card" Type="Cardinality">N</TemplateValue></Part>` —— N 输入的或门(in1..inN / out)。并联触点共享一个 `<Wire>`(children = `<Powerrail/>` + 各 `<NameCon Name="in"/>`),各触点 `out` 接到 O 的 `in1`/`in2`,O 的 `out` 接后续。
- **调用块(Call)**:
  `<FlgNet xmlns="…/FlgNet/v5"><Parts><Call UId="21"><CallInfo Name="FC2" BlockType="FC" /></Call></Parts><Wires><Wire UId="22"><Powerrail /><NameCon UId="21" Name="en" /></Wire></Wires></FlgNet>`
  —— `<CallInfo Name= BlockType="FC|FB|…">` 指定被调块;`<Wire><Powerrail/>→<NameCon Name="en"/>` 给 EN 上电。UId 每个网络内唯一即可(各网络可复用 21/22)。
- 自保持 `Motor := (Start OR Motor) AND NOT Stop` 的 LAD = Contact(Start,NO) ‖ Contact(Motor,NO) 经 O 部件 → Contact(Stop,NC) → Coil(Motor),共 9 根 wire。
  **完整可复制 XML 全文 + 9 根 wire 逐条解释**见 [`启保停LAD范例`](../write-program/examples/启保停LAD范例.md)。
- **IEC 定时器/计数器(TON/TOF/TP…)** = **带 `Version` 的 `<Part>`,不是 Call**:
  ```xml
  <Part Name="TON" Version="1.0" UId="30">
    <Instance Scope="LocalVariable" UId="31"><Component Name="DelayTimer" /></Instance>
    <TemplateValue Name="time_type" Type="Type">Time</TemplateValue>
  </Part>
  ```
  - **`Version="1.0"` 必须加在 `<Part>` 上**(不是 CallInfo);**只有带 Version 的 Part 才允许 `<Instance>` 子元素**(漏 Version → "Part has invalid child Instance")。
  - 实例在 **Static** 段声明 **`TON_TIME`**;接点只有 **`IN`/`PT`(输入)、`Q`/`ET`(输出),没有 `en`/`eno`**。`Q` 可直驱线圈 `in`;不用的 `ET` 接 `<OpenCon UId="x" />`。
  - 完整可复制 XML + 13 根 wire 逐条见 [`定时器范例`](../write-program/examples/定时器范例.md)(导入 + 编译 0 错误)。SCL 版同文件(逻辑首选)。

## GRAPH(S7-GRAPH)

- **线性顺控直接走路径 C**:`tia_block_write_code` 的 sequence spec 已在真机验证(导入+编译 0 错)。从零手写只在超出线性拓扑(并行/分支/跳转)时才需要,此时用模板法。
- **可往返**:`tia_block_export` 一个 GRAPH FB → 改 `<Name>`/`<Number>` → `tia_block_import` → 编译 0 错误。
- GRAPH SimaticML 是最大最复杂的 schema(TIA 导入时自动补全约 2000 行运行时接口:RT_DATA/步标志/偏移量…,手写最小版不用管);**复杂拓扑不要从零手敲**,用**模板法**(导出相似 GRAPH 块,改步/转换/动作,重导入)。
- 2026-08 真机(V21/S7-1511)实测的从零最小形态要点(全部写进 `GraphSpecGenerator`):`<MemoryLayout ReadOnly="true">Standard</MemoryLayout>`;体在 `NetworkSource` 下 `Graph xmlns="…/NetworkSource/Graph/v6"`(其内 FlgNet 也归 Graph 命名空间,别用 FlgNet/v5);步-转移**成对编号**(1,21,32,43…=11i+10);每步必备 `Supervisions`(条件→`SvCoil`)与 `Interlocks`(→`IlCoil`);转移条件→`TrCoil`;序列以 `Transition→EndConnection` 终结;动作操作数是 `<Token Text="#局部名"/>`(带 #);步/转移名必须唯一;接口需 `OFF_SQ/INIT_SQ/ACK_EF` 输入。参考样板:`tests/fixtures/FB_GraphDemo.xml`(真机导出)。
- **循环顺控**(2026-08-23 真机验证):write_code spec 加 `"loop": true`(读回对称:`tia_block_read_code` 的 graph 视图对闭环顺控报 `loop: true`);手写 XML 时把尾连接的 `EndConnection` 换成回连首步的 **Jump 连接**:
  ```xml
  <Connection>
    <NodeFrom><TransitionRef Number="54" /></NodeFrom>  <!-- 尾转移号 -->
    <NodeTo><StepRef Number="1" /></NodeTo>              <!-- 初始步号 -->
    <LinkType>Jump</LinkType>
  </Connection>
  ```
  (`LinkType` 只有 `Direct`/`Jump` 两值;Direct 回连会报 "The sequencer does not start with a step"。)
- **每步至多 1 个动作**(2026-08-23 实测 TIA V21 XML 导入上限):多 `<Action>` 并列报 "action table … line break";`<NewLine/>` 行分隔报 "not supported"(xsd 允许但 Import 拒);单 Action 多 Token 导入能过但编译报 action 语法错。多驱动的操作数放调用 OB 合并,或拆成独立步。
- **运行时状态位**:GRAPH 接口**没有** `EN_SQ`(编译报 not defined);步激活标志在 TIA 自动追加的步实例(`G7_StepPlus_V6`)上,如背景 DB 的 `StpA.X`(全 0=序列到 End)。`INIT_SQ` 置位可重启回初始步。
- 全新顺序逻辑,有时用 SCL 状态机(`CASE step OF`)更快;GRAPH-native 适合克隆/改造现有 GRAPH。

## UDT(SW.Types.PlcStruct)

**最可靠:导出一个相似 UDT 当模板(`tia_block_export` 现在支持 UDT)→ 改 → `tia_udt_import`**;别从零手敲——下面这些坑都是手敲踩出来的。`tia_block_export` 导出的就是规范形态,照着改不会错。

### 最小可导入形态(实测过)

```xml
<?xml version="1.0" encoding="utf-8"?>
<Document>
  <Engineering version="V21" />
  <SW.Types.PlcStruct ID="0">
    <AttributeList>
      <Interface>
        <Sections xmlns="http://www.siemens.com/automation/Openness/SW/Interface/v5">
          <Section Name="None">
            <Member Name="iStationNo" Datatype="Int">
              <Comment><MultiLanguageText Lang="zh-CN">工位编号</MultiLanguageText></Comment>
              <StartValue>0</StartValue>
            </Member>
            <Member Name="xCmd" Datatype="Bool">
              <Comment><MultiLanguageText Lang="zh-CN">指令</MultiLanguageText></Comment>
            </Member>
            <Member Name="aStates" Datatype="Array[1..16] of Bool">
              <Comment><MultiLanguageText Lang="zh-CN">状态位数组</MultiLanguageText></Comment>
            </Member>
            <Member Name="child" Datatype="&quot;OtherType&quot;"><!-- 引用另一 UDT,带引号 -->
              <Comment><MultiLanguageText Lang="zh-CN">子结构</MultiLanguageText></Comment>
            </Member>
          </Section>
        </Sections>
      </Interface>
      <Name>typeMyStruct</Name>
      <Namespace />
    </AttributeList>
  </SW.Types.PlcStruct>
</Document>
```

### ⚠️ 手敲必踩的坑(每条都实测报错过)

1. **成员注释是元素文本,不是 `Text=` 属性**:正确 `<MultiLanguageText Lang="zh-CN">文本</MultiLanguageText>`;写成 `<MultiLanguageText Lang="zh-CN" Text=".." />` 报 `'Text' attribute is not declared`。**注释 culture 用项目里已有的**(默认新项目仅 en-US):zh-CN 注释在无 zh-CN 的项目里,tag 表 import 显式报错、块 import **静默剥离**;en-US culture 装中文文本显示正常(2026-08-23 真机实测)。
2. **`<StartValue>` 不带 `SystemString`**:正确 `<StartValue>0</StartValue>`;写 `<StartValue SystemString="false">0</StartValue>` 报 `'SystemString' attribute is not declared`。可整段省略(用类型默认值)。
3. **`<Sections>` 必须在 `<Interface>` 内**(`<Interface>` 在 `<AttributeList>` 内);PlcStruct 的 `<AttributeList>` 必须有 `<Namespace />`——否则 `tia_udt_import` 抛 EngineeringTargetInvocationException。
4. **类型注释(ObjectList)用 `MultilingualTextItem`,不是 `MultiLanguageText`**:ObjectList 注释是 `<MultilingualText CompositionName="Comment"><ObjectList><MultilingualTextItem ID=".." CompositionName="Items"><AttributeList><Culture>zh-CN</Culture><Text>..</Text></AttributeList></MultilingualTextItem></ObjectList></MultilingualText>`;写成裸 `<MultiLanguageText>` 报 `class ... not supported`。**最省事:整个 ObjectList 省掉**(类型注释可选)。
5. `Remanence`、成员 `<AttributeList>` 里的 `BooleanAttribute`(ExternalAccessible/Visible/Writable、SetPoint)都**可选**——导出带、导入没也行。
6. **ObjectList 里的 `<MultiLanguageText>`/`MultilingualTextItem` 要带唯一 `ID`**(从 1 递增);漏 ID 报 `Cannot find the required 'ID' attribute`。成员注释(`<Comment>` 里的)则不需要 ID。

### 多类型一次导入(批量搬运用)

一个 `<Document>` 下放多个 `<SW.Types.PlcStruct>` 兄弟(各 `ID` 全文档唯一,如 0/1000/2000),`tia_udt_import` 一次导多个。
- ⚠️ **必须拓扑序(依赖在前)**:Siemens 拒绝前向引用——`ParentType.ChildField` 引用 `ChildType`,则 `ChildType` 要排在文件前面,否则 `Data type "ChildType" is unknown`。批量拓扑合并脚本见 [`跨项目搬运`](../migrate-project/SKILL.md) 的 `merge.ps1`。
- `sourceXml` **可传文件路径**,不必内联;`plcPath` 带 `/typegroup:NAME` 导入到子组(不指定→根),导入子组 = 移动(先删同名,再建)。

## 维护
更多细节(各 token 全集、规范化行为)见 `brands/tia/mcp/docs/P3-openness-notes.md`。
LAD 自保持 XML 全文范例已补:[`启保停LAD范例`](../write-program/examples/启保停LAD范例.md)。
TODO:补 LAD 多分支(嵌套 OR/AND、定时器/计数器部件)与 Call 带形参的完整范例。

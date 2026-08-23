# 范例 · 从零手写一个 LAD 启保停(self-hold)

> 何时用:想照着一个**完整、实测过、可直接复制**的例子,用 SimaticML(路径 B)手写一个 LAD 梯级。
> 这是 [`写程序`](../SKILL.md) 路径 B 的配套范例;FlgNet 各部件的含义见
> [`SimaticML速查`](../../_reference/simaticml-reference.md)。

**环境**:TIA Portal V21,CPU 1511-1 PN(`6ES7 511-1AK02-0AB0/V2.9`)。导入 + 编译 0 错误。

## 目标梯级

经典启保停,一根梯级:

```
     Start_PB        Stop_PB        Motor
 ┌────┤ ├────┐────────┤/├──────────( )
 │            │
 │   Motor    │
 └────┤ ├────┘
```

逻辑等价于 `Motor := (Start_PB OR Motor) AND NOT Stop_PB`。
(纯新写 SCL 逻辑其实用路径 A 更省事 —— 见 `写程序.md`。本例专门演示**手写 LAD**。)

## 前置

1. 项目 + PLC 已就绪(见 [`连接与项目管理`](../../connect-project/SKILL.md) / [`硬件组态`](../../hardware-config/SKILL.md))。
2. 三个 tag 用 `tia_tag_create` 建好(FlgNet 用**符号名**引用,不是绝对地址):

   | 名称 | 地址 | 类型 |
   |------|------|------|
   | `Start_PB` | `%I0.0` | Bool |
   | `Stop_PB`  | `%I0.1` | Bool |
   | `Motor`    | `%Q0.0` | Bool |

## 关键结构(看懂下面这段 FlgNet 就够了)

- **触点/线圈是 `<Part>`**:常开 = `<Part Name="Contact">`;常闭 = 加 `<Negated Name="operand"/>`;线圈 = `<Part Name="Coil">`。
- **并联(OR)是 `<Part Name="O">`**,`<TemplateValue Name="Card" Type="Cardinality">2</TemplateValue>` 表示 2 路输入(in1/in2/out)。
- **操作数是 `<Access>`**(`Scope="GlobalVariable"` + `<Symbol><Component Name="标签名"/></Symbol>`),通过 `<Wire><IdentCon UId=访问/><NameCon UId=部件 Name="operand"/></Wire>` 喂给部件。
- **接线是 `<Wire>`**:`<Powerrail/>` = 左母线;`<NameCon UId=部件 Name="in|out|in1|in2"/>` = 部件接点。
- **UId 只需网络内唯一**;导入后 TIA 会规范化并补全装饰属性(HeaderAuthor、Accessibility 等),无需手写。

## 完整可复制 XML(最小版,实测可导入)

把下面整段作为 `tia_block_import` 的 `source`(块名传 `FC_MotorLAD`,`plcPath` 传 `…/plc:program`):

```xml
<?xml version="1.0" encoding="utf-8"?>
<Document>
  <Engineering version="V21" />
  <SW.Blocks.FC ID="0">
    <AttributeList>
      <AutoNumber>true</AutoNumber>
      <HeaderVersion>0.1</HeaderVersion>
      <Interface>
        <Sections xmlns="http://www.siemens.com/automation/Openness/SW/Interface/v5">
          <Section Name="Input" />
          <Section Name="Output" />
          <Section Name="InOut" />
          <Section Name="Temp" />
          <Section Name="Constant" />
          <Section Name="Return">
            <Member Name="Ret_Val" Datatype="Void" />
          </Section>
        </Sections>
      </Interface>
      <MemoryLayout>Optimized</MemoryLayout>
      <Name>FC_MotorLAD</Name>
      <Namespace />
      <Number>0</Number>
      <ProgrammingLanguage>LAD</ProgrammingLanguage>
    </AttributeList>
    <ObjectList>
      <SW.Blocks.CompileUnit ID="1" CompositionName="CompileUnits">
        <AttributeList>
          <NetworkSource>
            <FlgNet xmlns="http://www.siemens.com/automation/Openness/SW/NetworkSource/FlgNet/v5">
              <Parts>
                <Access Scope="GlobalVariable" UId="21"><Symbol><Component Name="Start_PB" /></Symbol></Access>
                <Access Scope="GlobalVariable" UId="22"><Symbol><Component Name="Motor" /></Symbol></Access>
                <Access Scope="GlobalVariable" UId="23"><Symbol><Component Name="Stop_PB" /></Symbol></Access>
                <Access Scope="GlobalVariable" UId="24"><Symbol><Component Name="Motor" /></Symbol></Access>
                <Part Name="Contact" UId="25" />
                <Part Name="Contact" UId="26" />
                <Part Name="O" UId="27">
                  <TemplateValue Name="Card" Type="Cardinality">2</TemplateValue>
                </Part>
                <Part Name="Contact" UId="28"><Negated Name="operand" /></Part>
                <Part Name="Coil" UId="29" />
              </Parts>
              <Wires>
                <Wire UId="30"><IdentCon UId="21" /><NameCon UId="25" Name="operand" /></Wire>
                <Wire UId="31"><IdentCon UId="22" /><NameCon UId="26" Name="operand" /></Wire>
                <Wire UId="32"><IdentCon UId="23" /><NameCon UId="28" Name="operand" /></Wire>
                <Wire UId="33"><IdentCon UId="24" /><NameCon UId="29" Name="operand" /></Wire>
                <Wire UId="34"><Powerrail /><NameCon UId="25" Name="in" /><NameCon UId="26" Name="in" /></Wire>
                <Wire UId="35"><NameCon UId="25" Name="out" /><NameCon UId="27" Name="in1" /></Wire>
                <Wire UId="36"><NameCon UId="26" Name="out" /><NameCon UId="27" Name="in2" /></Wire>
                <Wire UId="37"><NameCon UId="27" Name="out" /><NameCon UId="28" Name="in" /></Wire>
                <Wire UId="38"><NameCon UId="28" Name="out" /><NameCon UId="29" Name="in" /></Wire>
              </Wires>
            </FlgNet>
          </NetworkSource>
          <ProgrammingLanguage>LAD</ProgrammingLanguage>
        </AttributeList>
      </SW.Blocks.CompileUnit>
    </ObjectList>
  </SW.Blocks.FC>
</Document>
```

### 9 根 wire 在干什么

| Wire | 连接 | 作用 |
|------|------|------|
| 30–33 | `IdentCon` 访问 → 部件 `operand` | 给 4 个触点/线圈绑定标签 |
| 34 | `Powerrail` → 25.in + 26.in | 左母线同时进 Start、Motor 两个并联触点 |
| 35 | 25.out → 27.in1 | Start 触点出 → OR 第 1 路 |
| 36 | 26.out → 27.in2 | Motor 触点出 → OR 第 2 路(自保持回路) |
| 37 | 27.out → 28.in | OR 出 → Stop 常闭触点 |
| 38 | 28.out → 29.in | Stop 出 → Motor 线圈 |

## 校验

```
tia_project_compile(scopePath=…/device:PLC_1, mode=Software)
→ success:true, errors:0   # "Block was successfully compiled. FC_MotorLAD (FC0)"
```

> ⚠️ 若 CPU 没插 IO 模块,会有一条 warning "Inputs or outputs … do not exist in the configured hardware" —— 不影响逻辑编译,组态了 DI/DO 模块后即消失。
> ⚠️ FC/FB 要真正运行,需在循环 OB(Main/OB1)里调用它(LAD `Call`,见速查的 Call 配方)。

## 踩过的坑(都已实测)

| 现象 | 修法 |
|------|------|
| 导入报 `Missing 'Namespace' identifier attribute` | `<AttributeList>` 里加 `<Namespace />`(FC/FB/UDT 都要) |
| 想覆盖已有的 Main/同名块 | 直接重导入即可——`tia_block_import` 会**覆盖同名块**(ImportOptions.Override,幂等);只有要移除块时才 `tia_block_delete` |
| 不知道操作数怎么写绝对地址 | 别写绝对地址 —— 先 `tia_tag_create` 建符号 tag,FlgNet 用 `Scope="GlobalVariable"` + `<Component Name="标签名"/>` 引用 |

> 💡 新捷径:本范例的启保停逻辑现在可以直接用 `tia_block_write_code` 的结构化 spec 写
> (logic = and(or(Start_PB, Motor), NOT Stop_PB) → coil Motor),`tia_block_read_code` 能把它读回
> 同一表达式。手写 FlgNet 仍适合超出 v1 指令集的场景。

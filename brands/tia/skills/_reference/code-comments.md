# 代码注释规范

> 何时用:写 / 改任何 PLC 代码(逻辑块、UDT、变量 tags)或往 skill 里贴示例代码时,按本篇给注释。本篇是 TIA 品牌层的详细写法;通用方法论见 [`skills/overview`](../../../../skills/overview/SKILL.md) 的「代码注释」。

## 两条铁律

1. **所有代码必须有清晰注释** —— 块 / UDT 顶部要有用途说明,每个管脚 / 成员 / 关键逻辑要有一句注释。显而易见的语法不强制(别给 `END_VAR` 也加注释)。
2. **注释一律用中文** —— 英文只留给关键字、标识符、订货号等不可避免处。`// delay timer` 这种英文注释要改成 `// 通电延时定时器`。

> 这两条同时约束:**agent 按 skill 产出的 PLC 代码**,以及 **skill 文档 / examples 里贴的示例代码**(示例本身就是范本,更不能漏)。

## 注释要写什么

写三样东西,挑相关的写:

- **做什么** —— 这段逻辑 / 这个变量的意图。
- **为什么** —— 非显而易见的设计决策、规避的坑(例:`// 用 TON 而非 SD,IEC 定时器可在线改 PT 且不占 S7 资源`)。
- **非常规处** —— 魔法数、时序前提、与参考工程的差异。

**别写废话**:复述代码的注释一文不值。

```scl
// 反面(废话):
#iStationNo := 1;   // 给 iStationNo 赋 1

// 正面(说意图):
#iStationNo := 1;   // 本工位固定为 1 号站(上线前按产线配置,见 IO 点表)
```

## 分语言写法

| 语言 | 注释机制 | 范例 |
|---|---|---|
| **SCL** | 行注释 `//`、块注释 `(* … *)` | 块头一段 `(* … *)` 说用途;管脚 / 变量行尾 `//` 说作用;关键逻辑行 `//` 说意图 |
| **LAD / FBD** | 网络标题 + 网络注释(Network Comment) | 标题写动作,网络注释写联锁条件;SimaticML 里是 `<MultiLanguageText Lang="zh-CN">` |
| **GRAPH** | 步 / 转换 / 动作的注释 | 同样走 `Lang="zh-CN"`;步注释写"这步在干什么" |
| **UDT / 变量(tags)** | 成员注释 / 变量表注释列 | `<Comment><MultiLanguageText Lang="zh-CN">…` ;变量表 Comment 列填中文 |

### SCL 范例(行尾中文注释)

```scl
(* FB_DelayLamp:多条件驱动一个通电延时定时器,到时点亮指示灯。
   定时器用 IEC 的 TON(可在线改 PT),单实例放 Static,勿放 Temp。*)
FUNCTION_BLOCK "FB_DelayLamp"
{ S7_Optimized_Access := 'TRUE' }
VERSION : 0.1
   VAR_INPUT
      Start : Bool;       // 启动按钮(常开,ON 触发)
      Auto  : Bool;       // 自动模式到位信号
      Stop  : Bool;       // 停止按钮(常闭,断开即停)
   END_VAR
   VAR_OUTPUT
      Lamp  : Bool;       // 输出:延时到点亮
   END_VAR
   VAR
      DelayTimer : TON_TIME;   // IEC 通电延时定时器(单实例,放 Static)
   END_VAR
```

> 风格沿用 [`定时器范例`](../write-program/examples/定时器范例.md)。

### UDT / 变量注释范例(SimaticML)

成员注释是**元素文本**(`MultiLanguageText` 的内容),语言固定 `zh-CN`:

```xml
<Member Name="iStationNo" Datatype="Int">
  <Comment><MultiLanguageText Lang="zh-CN">工位编号</MultiLanguageText></Comment>
  <StartValue>0</StartValue>
</Member>
```

> `Lang="zh-CN"` 是本知识库的标准写法,完整 schema 见 [`SimaticML速查`](simaticml-reference.md) 的「UDT」节(成员注释是元素文本而非 `Text=` 属性,这是手敲六大坑之一)。

## 注释粒度(最低要求)

- **块 / UDT 顶部**:一段用途说明(这个块 / 类型是干嘛的、谁调用它)。
- **每个管脚(in / out / inout / stat / temp)**:一句说作用;裸 `Bool` / `Int` 不加注释看不懂语义。
- **每个 UDT 成员**:一句注释(成员注释会被 `tia_interface_read` 读回,是接口文档的一部分)。
- **关键逻辑行 / 网络**:注释意图,不是翻译指令。
- **显而易见的代码**:不强制(别为了凑数加噪音)。

## 好 / 坏对比

**坏** —— 英文注释 + 无管脚说明,读的人不知道每个 Bool 干嘛:

```scl
FUNCTION_BLOCK "FB_X"
   VAR_INPUT
      a : Bool;   // input a
      b : Bool;
   END_VAR
   VAR
      t : TON_TIME;
   END_VAR
```

**好** —— 中文注释,管脚 / 定时器意图清楚:

```scl
FUNCTION_BLOCK "FB_X"
   VAR_INPUT
      a : Bool;   // 启动条件(ON 有效)
      b : Bool;   // 复位信号(ON 复位)
   END_VAR
   VAR
      t : TON_TIME;   // 上电延时,避免抖动误触发
   END_VAR
```

## 相关

- [`命名规范`](naming.md) —— 块 / 网络 / 地址怎么起名(注释与命名配套)。
- [`SimaticML速查`](simaticml-reference.md) —— UDT / LAD / GRAPH 的 XML schema 与手敲坑。
- [`定时器范例`](../write-program/examples/定时器范例.md) / [`启保停LAD范例`](../write-program/examples/启保停LAD范例.md) —— 已合规的注释范本。

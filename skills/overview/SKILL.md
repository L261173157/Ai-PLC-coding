---
name: overview
description: "通用 PLC 工程方法论总览(品牌中性)。先读这篇,再按品牌选对应的工具映射 skill。"
---

# PLC 工程方法论 · overview

这是**通用方法论层**:品牌中性的能力词汇与工作流,讲"做什么、按什么顺序",不涉及任何厂商工具。
具体品牌的工具调用与专属坑(`tia_*`、SimaticML、Studio 5000 的 AOP…)在 `brands/<brand>/skills/<area>/SKILL.md`。

## 分层模型
- 通用方法论(本层):`skills/<area>/SKILL.md` —— "该怎么做"的工程套路。
- 品牌工具映射:`brands/<brand>/skills/<area>/SKILL.md` —— "用哪个工具、传什么参、避哪个坑"。
- 公司标准:`standards/<company>/<framework>/` —— 某客户/公司的具体程序框架(命名、UDT 分类、报警、MES…)。

## 典型顺序(几乎所有 PLC 工程通用)
1. **连接** —— 连上控制器 / 打开工程,选对安全模式。
2. **硬件** —— 先配硬件(CPU、模块、网络、IO 地址),再写程序。
3. **编程** —— 先数据类型与变量,再逻辑块,编译到 0 错误。
4. **复用** —— 从库/母版实例化标准设备逻辑,别重写。
5. **迁移** —— 把参考工程的类型层与库块批量搬入新工程。

跳步的典型代价:先写程序后配硬件 → IO/地址全乱、变量返工;不复用 → 每个站重写气缸/伺服逻辑、风格不一。

## 安全模式(写之前先想清楚)
| 模式 | 允许 | 用途 |
|---|---|---|
| 只读 | 读 + 编译 | 摸底、读程序、看结构 |
| 读写 | + 建块/变量/硬件、保存 | 写程序、组态 |
| 不受限 | + 下载 / RUN / STOP(需二次确认) | 真机调试 |

⚠️ 默认档通常是**读写**不是只读(见品牌 skill);真机动作(下载、运行、停车)一定走不受限 + 确认,且先在离线/Fake 验证。

## 代码注释

所有 PLC 代码(逻辑块、数据类型、变量)必须有**清晰注释**,且注释一律用**中文**。块 / 类型顶部写用途,每个管脚 / 成员一句注释,关键逻辑注释意图——别写复述代码的废话。

这是品牌中性的铁律;具体每种语言怎么加注释(文本 `//`、梯形图网络注释、各品牌的注释机制)见你品牌的 write-program / `_reference`。

## 怎么用这套知识库
1. 读本篇 + 你品牌的 `brands/<brand>/skills/overview/`。
2. 按上面顺序,每一步先读对应 area 的通用 skill(本层)再读品牌 skill。
3. 若有公司标准,以 `standards/` 的框架为准(命名、结构、报警机制)。

## 区名映射(通用 → 品牌)
| 通用 | 西门子 TIA |
|---|---|
| `skills/overview/` | `brands/tia/skills/overview/` |
| `skills/connect/` | `brands/tia/skills/connect-project/` |
| `skills/hardware/` | `brands/tia/skills/hardware-config/` |
| `skills/write-program/` | `brands/tia/skills/write-program/` |
| `skills/reuse/` | `brands/tia/skills/reuse-library/` |
| `skills/migrate/` | `brands/tia/skills/migrate-project/` |

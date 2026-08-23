# 通用 PLC 工程方法论(skills/)

本目录是**品牌中性**的方法论层:描述 PLC 开发"做什么、按什么顺序",不含任何厂商专属工具调用。
具体品牌的工具映射(`tia_*`、SimaticML、品牌专属坑)在 [`brands/<brand>/skills/`](../brands/) 下。

## 分层模型
- 通用方法论(此处):`skills/<area>/SKILL.md`
- 品牌工具映射:`brands/<brand>/skills/<area>/SKILL.md`

## area 与品牌目录映射
| 通用目录 | 西门子 TIA 目录 |
|---|---|
| `skills/overview/` | `brands/tia/skills/overview/` |
| `skills/connect/` | `brands/tia/skills/connect-project/` |
| `skills/hardware/` | `brands/tia/skills/hardware-config/` |
| `skills/write-program/` | `brands/tia/skills/write-program/` |
| `skills/reuse/` | `brands/tia/skills/reuse-library/` |
| `skills/migrate/` | `brands/tia/skills/migrate-project/` |

(品牌目录用更具体的名,通用层用短动词;新增品牌时在此表加一列。)

> 本层只写品牌中性方法论;具体工具调用与品牌坑在 `brands/<brand>/skills/`。

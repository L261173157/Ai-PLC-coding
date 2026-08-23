# plc-base 插件

品牌中性的 PLC 工程方法论 skills(连接 / 硬件 / 编程 / 复用 / 迁移 / 总览)。需配合一个**品牌插件**(如 `plc-siemens`)使用——后者提供具体 MCP 工具与品牌工具映射 skill。

本插件**不含 MCP server**,故无 `.mcp.json`;skill 由 `skills/<name>/SKILL.md` 前言发现。

## 开发(dev,就地)
skills 源在仓库根 `../../skills/`。本插件目录下的 `skills/` 由 [`scripts/dev-sync-plugins.ps1`](../../scripts/dev-sync-plugins.ps1) 同步而来(派生,见根 `.gitignore`)。

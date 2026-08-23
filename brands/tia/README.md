# 西门子 TIA 品牌

本目录是**西门子 TIA Portal** 品牌单元,多品牌 monorepo 中的一个品牌。

- `mcp/` —— 能力层:TiaMcp .NET 解决方案(`TiaMcp.slnx` + 5 个项目:Contract / Fake / Openness / Openness.Worker(net48) / Server)。把 TIA Portal Openness V21 封装成 MCP server。
- `skills/` —— 品牌知识层:TIA 工程技能(`tia_*` 工具映射、SimaticML、V21 专属坑)+ `_reference/`。

被 [`plugins/plc-siemens/`](../../plugins/plc-siemens/) 消费(MCP server + 品牌 skills 打包成一个 Claude Code 插件)。
品牌中性的通用方法论见仓库根 [`skills/`](../../skills/)。

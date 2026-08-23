---
name: overview
description: "任何人(或 agent)第一次接触本知识库时先读这篇,搞清三层架构、各 skill 的分工、做一个真实 PLC 任务的典型顺序。"
---

# TIA PLC 工程知识库 · 总览

## 这是什么

本仓库分三层:

```
┌─ 编排层 (Agent) ── Claude Code / Cursor / 自研 agent ───────────┐
│   读「知识层」手册,按步骤调「能力层」工具,完成用户的 PLC 任务   │
├─ 知识层 (Skills) ── 本目录 skills/ 下的中文工作手册 ─────────────┤
│   把 MCP 工具(原语)→ 真实工程工作流(组态 / 编程 / 复用)        │
├─ 能力层 (MCP) ── TiaMcp server(mcp/,51 个工具,agent 无关) ──┤
│   连接 / 项目 / 硬件 / 网络 / 块读写 / tags / 导入 / 库 / 在线     │
└─ 物理层 ── net48 worker → Siemens.Engineering → TIA Portal V21 ─┘
```

- **能力层(MCP)** 只提供「能做什么」(纯机制,不含工程套路)。它的工具清单见 [`工具清单`](../_reference/tool-catalog.md)。
- **知识层(skills)** 提供「该怎么做」——按什么顺序、什么参数、避哪些坑。就是本目录这几篇。
- **编排层(agent)** 拿用户的具体需求,读 skill,调 MCP 工具完成。

> 部署:能力层(MCP server)需要装在有 TIA Portal V21 的机器上,详见 [`connect-project`](../connect-project/SKILL.md)。知识层是纯 markdown,任何 agent 都能读。

## 典型任务顺序

做一个完整 PLC 程序,通常按这个链路走,每步对应一篇 skill:

1. **连接 + 开项目** → [`连接与项目管理`](../connect-project/SKILL.md)
2. **硬件组态**(CPU / 模块 / 网络 / 地址)→ [`硬件组态`](../hardware-config/SKILL.md)
3. **写程序**(tags / UDT / SCL / LAD / GRAPH)→ [`写程序`](../write-program/SKILL.md)
4. **复用已有模块**(从全局库实例化标准设备块)→ [`复用已有模块`](../reuse-library/SKILL.md)
   - **整批搬运**(把一个现成参考项目的 UDT/块批量搬过来)→ [`跨项目搬运`](../migrate-project/SKILL.md)
5. **编译校验** → 见各 skill 末尾的「校验」小节(统一用 `tia_project_compile`,直到 0 错误)

只读「看懂一个现成项目」从 [`只读通读Demo`](examples/只读通读Demo.md) 入手;
端到端「从零搭一个站」见 [`从零搭一个站`](examples/从零搭一个站.md)。

## 通用约定

- **安全模式**:写操作需 `--mode ReadWrite`,在线/删除等需 `Unrestricted` + `confirm=true`。详见 [`安全与模式`](../_reference/safety-modes.md)。
- **命名规范**:`G_*` 可复用设备块 vs `OP<NN>_*` 站点实例、网络名、地址方案,见 [`命名规范`](../_reference/naming.md)。
- **代码注释规范**:写 / 改任何代码(逻辑块、UDT、变量、示例)必须有清晰中文注释——SCL `//`、LAD 网络注释、UDT 成员 `Lang="zh-CN"`。详见 [`代码注释规范`](../_reference/code-comments.md)。
- **每篇 skill 顶部**都有一行「> 何时用:…」,据此判断要不要读它。
- 标 `TODO:` 的小节是待补充内容;标 `⚠️ 未验证` 的步骤尚未在真实 TIA 上跑通,用前需谨慎。

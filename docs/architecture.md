# 架构:能力层 / 知识层 / 编排层

本仓库是**多品牌 monorepo**;本篇描述西门子 TIA 品牌(`brands/tia/`)的分层。把"用 AI 驱动 PLC 工程"拆成三层(能力 / 知识 / 编排),职责严格分离。品牌中性的通用方法论在仓库根 `skills/`,插件打包在 `plugins/`。公司标准**不进本仓库**:`standards/` 只是挂载点,内容由独立上层私有库复制/链接进来(`standards/README.md`)。

```
┌─ 编排层 (Agent) ── Claude Code / Cursor / 自研 agent ───────────┐
│   拿用户需求,读「知识层」手册,按步骤调「能力层」工具完成任务    │
├─ 知识层 (Skills) ── brands/tia/skills/ 下的中文工作手册(纯 markdown) ──────┤
│   把 MCP 工具(原语)→ 真实工程工作流(组态/编程/复用),内嵌配方  │
├─ 能力层 (MCP) ── brands/tia/mcp/ 下的 TiaMcp server(49 工具,agent 无关) ─┤
│   连接/项目/硬件/网络/块读写/tags/导入/库/在线                    │
└─ 物理层 ── net48 worker → Siemens.Engineering → TIA Portal V21 ─┘
```

## 各层职责

### 能力层(MCP)— `brands/tia/mcp/`
纯**机制**:提供"能对 TIA 做什么"的原子操作,**不含工程策略**(不替你决定先配硬件还是先写程序)。要求:TIA-操作完备、agent 无关。内部结构(seam/bridge/worker)见 `../CLAUDE.md` 与 `brands/tia/mcp/docs/P3-openness-notes.md`:
- `TiaMcp.Contract` 单一接缝(ITiaBackend + DTO + RPC + TiaPath)。
- `TiaMcp.Server`(net10)MCP 宿主,工具层只依赖 Contract。
- `TiaMcp.Fake`(net10)离线内存后端。
- `TiaMcp.Openness`(net10)桥,转发到 worker。
- `TiaMcp.Openness.Worker`(net48)唯一加载 Siemens.Engineering 的进程。

### 知识层(Skills)— `brands/tia/skills/`
承载**"该怎么做"**的工程套路:按什么顺序、什么参数、避哪些坑。纯中文 markdown,任何 agent 都能读。不绑定具体 agent 运行时。TIA 品牌共 6 篇:总览(overview)、连接与项目管理(connect-project)、硬件组态(hardware-config)、写程序(write-program)、复用库(reuse-library)、迁移工程(migrate-project),加 `_reference/` 共享参考。品牌中性的通用方法论在仓库根 [`skills/`](../skills/)(与品牌 skill 分层对应)。

### 编排层(Agent)
具体 agent(Claude Code/Cursor/自研)。读知识层、调能力层,对用户的具体任务做决策。**不在本仓库实现**——本仓库交付能力层 + 知识层,任何 agent 都能组装。

## 为什么能力层必须 agent 无关

- **可移植**:同一个 MCP server 能被任意 agent 复用;知识沉淀在 markdown 而非代码里,演进互不影响。
- **可测试**:能力层有 Fake 后端可离线全量自测,不依赖某个 agent。
- **职责清晰**:机制(能力)与策略(知识)分开——改工程套路只动 markdown,扩 TIA 能力只动 MCP。

详见 memory `architecture-mcp-capability-agent-design`(借鉴 refcode 的 Openness API 配方,而非它的工作流)。

## 部署
能力层与知识层的分发方式见 [`deployment.md`](deployment.md)。本品牌的 Claude Code 插件在 `plugins/plc-siemens/`(skills + MCP 打包);通用方法论插件在 `plugins/plc-base/`。

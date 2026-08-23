# 安全与模式

> 何时用:搞清某个操作需要哪个模式、为什么被拒、`confirm` 怎么用、审计日志在哪。

## 三档模式

启动 server 时 `--mode`(或环境变量 `TIA_MCP_MODE`):

| 模式 | 放行 |
|------|------|
| `ReadOnly` | 只读工具 |
| `ReadWrite` | 读 + 写(导入/创建块与 tag、项目生命周期、加/配置设备、在线连接)+ 可做删除(需 confirm) |
| `Unrestricted` | 以上 + 真实硬件动作(download/run/stop,需 confirm) |

> ⚠️ **默认档是 `ReadWrite`**(不是 ReadOnly)——`Program.cs` 在未指定 `--mode` 时回退到 ReadWrite;`AccessMode` 枚举注释里"默认 ReadOnly"是过时的,以代码为准。`--selftest` 完全绕过 guard。

## 四类操作的门槛

- **只读**:任意模式可调(status/list/read/info/interface_read/cross_reference/catalog_search/hardware_read/compile/connect/open 等)。
- **写(ReadWrite)**:import/create 块与 tag、项目 save/save_as/create/archive/close、device_add/module_add/network_configure、online_connect/disconnect、udt_import/tagtable_import/block_generate_from_source/group_create、library_open、block_create_from_copy。
- **删除(ReadWrite + `confirm=true`)**:block_delete、tag_delete、device_delete、module_delete、subnet_delete。
- **危险/真实硬件(Unrestricted + `confirm=true`)**:download、plc_run、plc_stop。

## confirm 机制

删除 / 危险操作若不带 `confirm=true`,返回 `MutationResult{status=AwaitingConfirmation}` + 重调提示,**什么都不做**。要真正执行,带 `confirm=true` 重调。

## 返回约定

- 写工具一律返回结构化 `MutationResult`(Applied / Denied / AwaitingConfirmation / Failed)。
- 只读工具**绝不抛异常**给客户端:成功返回 DTO,失败返回 `ToolError{message,errorType}`(因为 MCP 客户端会把抛出的异常掩成笼统的 "An error occurred invoking…",真因丢失)。

## 审计日志

guard **放行的每个写操作**都追加到审计日志(JSONL,默认 `%TEMP%/tiamcp-audit/<日期>.jsonl`);拒绝/预览不记。

## ⚠️ V21 在线能力限制

V21 Openness **没有** go-online / CPU RUN-STOP API:`online_status`/`online_connect`/`online_disconnect`/`plc_run`/`plc_stop` 在**真实后端返回 NotSupported**(只在 Fake 模拟,供离线测面用),`download` 是唯一真实在线动作。别指望它们对真实硬件起作用。

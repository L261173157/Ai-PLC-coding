---
name: connect-project
description: "做任何 TIA 操作之前的第一步——连接 TIA Portal、打开/新建/保存/关闭项目、选对安全模式。这是其他所有 skill 的地基。"
---

# 连接与项目管理

## 前置条件(目标机一次性配置)

能力层(MCP server + net48 worker)所在机器必须满足:

1. 装好 **TIA Portal V21**(含 Openness)。
2. 当前 Windows 用户加入本地组 **`Siemens TIA Openness`**(加完要注销/重登一次刷新令牌),否则 `tia_connect` 抛 COMException。
3. 装 **.NET Framework 4.8 Developer Pack**(构建 net48 worker 用;仅开发机需要)。
4. Siemens DLL 解析靠 worker 的 `App.config` `<codeBase>` 指向 TIA 安装路径——细节见 [`P3-openness-notes`](../../mcp/docs/P3-openness-notes.md)。

## 选模式(安全档位)

- `--mode ReadOnly`:只读,agent 安全默认。
- `--mode ReadWrite`:导入/创建块与 tag、项目生命周期、加设备/组态——**写程序必须用它**。
- `--mode Unrestricted`:下载 / RUN / STOP 等真实硬件动作,且需 `confirm=true`。

⚠️ 默认档是 **ReadWrite**(不是 ReadOnly)。详见 [`安全与模式`](../_reference/safety-modes.md)。

## 连接(三种模式)

`tia_connect` 的 `mode`:

| mode | 含义 | 用途 |
|------|------|------|
| `attach` | 挂到一个**已经开着的 TIA GUI** 实例 | 最快最稳;**首选**,尤其 headless 卡死时 |
| `interactive` | 新开一个带界面的 Portal | 需要人看着 GUI 时 |
| `headless` | 新开一个无界面 Portal | 全自动批处理 |

**⚠️ attach 直接操作用户实时 GUI 项目**:写操作(建块、实例化、组态)会**真的改**那个项目,且**不会自动保存**。用 attach 做实验后,要么别保存、要么手动撤销/删除所建对象,以免污染用户工程。

**⚠️ headless 反复强杀会卡死**:多次强杀 Portal/worker 会残留 `Siemens.Automation.ObjectFrame.FileStorage.Server`,把 Siemens 的 IPC/许可栈搞坏,之后新 headless `Connect` 会卡住不返回(几分钟无响应)。遇到就改走 **attach**(让用户在 GUI 里开好项目),或重启机器清状态。

## 项目生命周期

| 工具 | 作用 |
|------|------|
| `tia_project_open` | 打开项目(`.ap21`;旧 `.ap18/.ap19` 会被 TIA 升级重组后另存为 `.ap21`)。`visible=false` 走无界面打开,适合批处理;路径在 server 层归一化为绝对路径 |
| `tia_project_status` | 读项目元信息(名/路径/版本/作者/是否改动/大小) |
| `tia_project_save` | 保存 |
| `tia_project_save_as` | 另存(V21 会把当前项目重绑到副本) |
| `tia_project_create` | 新建项目 |
| `tia_project_archive` | 归档 |
| `tia_project_close` | 关闭 |

**⚠️ 一个会话同时只能开一个项目**:要开/建新项目前,先 `tia_project_close` 当前项目,否则报 "Another project is already open"。

## 运维坑(踩过的)

- **别手动关 TIA Portal 窗口**:窗口一关,worker 持有的 `TiaPortal` 对象作废但字段非空,之后每次调用都抛异常,直到回收 worker(`/mcp` 重连)。整个会话保持窗口开着。
- **项目锁是 worker 进程持有的**,不是 GUI 窗口。重连时若提示"已被用户打开…等 2 分钟",真凶通常是上个会话残留的 `TiaMcp.Openness.Worker` 进程——杀掉它立即释放锁(否则傻等 2 分钟)。
- 重连后("/mcp")才能用上新版 worker 与新增工具。
- **重建 worker(改了 `OpennessEngine.cs` 后)**:`Stop-Process -Name TiaMcp.Openness.Worker`(跑着的 worker 锁了 `bin/Debug/net48/*.dll`,不杀会构建失败),再 `dotnet build brands/tia/mcp/src/TiaMcp.Openness.Worker/...csproj`;server 会**自动重启新 worker**,不必手动重启 MCP。
- **重启 worker 后第一次 `tia_connect` 常握手超时**(报 `EngineeringSecurityException ... The operation has timed out`,新 `TiaPortal` 实例首次握手慢)——**重试一次**就好,不是真错。
- **`tia_project_close` 偶发报 "project not found"**(项目其实开着,`tia_connect` 能看到)——路径解析偶发不稳,**重试一次**通常就成。

## 校验

- `tia_status` 应返回正常,且 `AccessMode` 与你启动的 `--mode` 一致。
- `tia_project_status` 能读到刚打开项目的名字/路径。

## 常见报错 → 修法

| 报错 | 原因 / 修法 |
|------|-------------|
| COMException(connect 时) | 用户没进 `Siemens TIA Openness` 组,或没注销重登 |
| "Another project is already open" | 先 `tia_project_close` |
| "已被用户 &lt;用户名&gt; 打开…2 分钟" | 杀掉上个会话残留的 worker 进程 |
| headless connect 卡死不返回 | 残留 Siemens 进程污染;清进程或重启,改用 attach |
| EngineeringObjectDisposed(所有调用都报) | TIA 窗口被关了;`/mcp` 重连 |
| 重启 worker 后 connect 报 `EngineeringSecurityException`+timed out | 新 worker 首次握手慢;**重试一次** connect |
| `tia_project_close` 报 project not found(项目其实开着) | 路径解析偶发不稳;**重试一次** |

## 状态

已实测:attach 连接 + 读取、open/save_as/close、创建项目。

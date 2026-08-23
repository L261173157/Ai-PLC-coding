---
name: reuse-library
description: "不想重写标准设备逻辑(伺服/气缸/阀/IO 监控…),想从全局库 .al21 把现成母版(Master Copy)实例化到项目里。"
---

# 复用已有模块

## 分层套路

真实机台程序常是两层:

- **可复用设备库块 `G_*`**(如 `G_FB<NNN>_<DeviceType>`)——与具体工位无关的通用设备逻辑,通常来自一个公司标准全局库。
- **站点实例 `OP<NN>_*`**——某工位对 `G_*` 的实例化 + 接线,由一个**调用 FC(如 `OP<NN>_Call`)**统一编排,再被循环 OB 调。

复用的本质 = 把库里的 `G_*` 母版实例化进当前 PLC,然后在站点逻辑里调用它。

## 前置条件

- 已连接 + 已打开项目;`--mode ReadWrite`。
- 手上有一个 `.al21` 全局库文件(没有的话,可在 TIA GUI 里建:右侧「库」→ 创建全局库 → 把块拖到「主副本」→ 保存)。

## 工作流

1. **打开库** `tia_library_open(libraryPath, readOnly)` —— 传 `.al21` 路径。
   - ⚠️ 库若**已被打开**(GUI 里开着、或当前会话先前已开过),旧版本会抛 "Cannot change the open mode of an already open global library";现已修复为**自动复用已打开的库**。
2. **列母版** `tia_mastercopy_list(libraryName)` —— 列出库里所有母版(名字 + 库内文件夹路径)。
3. **实例化** `tia_block_create_from_copy(plcPath, libraryName, masterCopyName)` —— 把某母版实例化成 PLC 里的新块,返回新块路径。
4. (按需)在站点 FC / OB 里**调用**这个新块,再编译。

## 已验证配方 & 坑

- **全链路验证**:open 一个 `.al21` 全局库 → list 母版 → create_from_copy 实例化成新块,链路通(attach 模式下会真写实时项目,见下坑)。
- **⚠️ attach 模式会真写实时项目**:`create_from_copy` 真的往用户正开着的项目里加了块(未保存)。实验后提醒用户**别保存 / 删掉**,保持工程干净。
- 名字推断:库名 = `.al21` 文件名去扩展名;`tia_mastercopy_list`/`create_from_copy` 的 `libraryName` 用这个。

## 校验

`tia_block_list` 能看到实例化出来的新块;`tia_project_compile` 0 错误(若已在 OB 里调用它)。

## 常见报错 → 修法

| 报错 | 原因 / 修法 |
|------|-------------|
| "no library" / 找不到库 | 库没 open;先 `tia_library_open` |
| "Cannot change the open mode…" | 库已开;新版自动复用(确认 worker 是最新版,`/mcp` 重连) |
| 找不到母版 | `tia_mastercopy_list` 先看准确母版名 |

## 状态

已实测:library_open / mastercopy_list / block_create_from_copy 全通,并修了「库已开」复用 bug。
TODO:`G_*` → `OP<NN>_*` 站点实例化 + `OP<NN>_Call` 编排的完整范式(等实际工程素材补)。

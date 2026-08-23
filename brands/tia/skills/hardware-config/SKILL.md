---
name: hardware-config
description: "给项目加 CPU、信号模块、组网(PROFINET/PROFIBUS)、分配 IP 与 IO 地址。写程序之前先把硬件配完。"
---

# 硬件组态

## 核心方法论:硬件优先

**先把硬件完整配好(CPU→模块→网络→地址),再建程序**。原因:程序里的 IO 引用、IO-System、设备名都依赖硬件已就位;硬件没配好,块编译会因找不到地址/设备而失败。

## 前置条件

- 已连接 + 已打开/新建项目(见 [`连接与项目管理`](../connect-project/SKILL.md))。
- `--mode ReadWrite`(加设备/组态都是写操作)。

## 工作流

1. **查目录** `tia_catalog_search` —— 用订货号/型号搜,拿到精确的 `typeIdentifier`(后续 add 要用)。
   - 例:搜 `6ES7 511` → `OrderNumber:6ES7 511-1CK00-0AB0/V2.9`。
   - ⚠️ **搜窄一点**:宽词(如只给 `6ES7 511`)可能命中上百条、返回十几万字符;尽量带**完整订货号**(如 `6ES7 511-1CK00`)缩小结果。
   - ⚠️ **同号不同型,挑准**:`6ES7 511` 同时命中**标准** `CPU 1511-1 PN`(`1AL03`)、**紧凑** `CPU 1511C-1 PN`(`1CK00`,自带 IO)、SIPLUS(`6AG1/6AG2`)。"CPU 1511-1 PN" 通常指**标准型 `1AL03`**,不是紧凑 `1CK00`——两者外形/IO/订货号都不同。
2. **加 CPU** `tia_device_add(projectPath, typeIdentifier, deviceName, deviceItemName)`
   - ⚠️ `deviceName` 给的是 **CPU 项**的名字;**站(station)会被命名为 `PLC_1`**。后面插模块要往 `PLC_1` 上插。
3. **插模块** `tia_module_add(projectPath, deviceName=PLC_1, typeIdentifier, slot?)`
   - DI/DQ/AI/AQ 各模块;`slot` 省略=下一个空槽(≥2;槽 1 是 CPU)。
4. **组网** `tia_network_configure(projectPath, deviceName, ipAddress, subnetMask, subnetName, ioSystemName, ...)`
   - PROFINET:`ipAddress`=IPv4(如 192.168.0.10),`ioSystemName`=IO-System 名。
   - PROFIBUS:`ipAddress`=站地址整数的字符串(如 "5"),`ioSystemName`=DP-MasterSystem。
   - **子网 / IO-System 不存在则自动创建**,已存在则连接——所以能在空项目上一步立起控制器。
5. **核对** `tia_hardware_read(projectPath)` —— 读回设备/子网/IO-System/节点(含 IP),确认配置对。
6. **编译硬件** `tia_project_compile` —— Hardware 应 **0 错误 0 警告**。

### 删除(对称能力)

- `tia_device_delete` / `tia_module_delete` / `tia_subnet_delete`(destructive,需 `confirm=true`)。

## 已验证配方 & 坑

- **参考序列**(headless 新建项目,全程 Applied、HW 编译 0/0):
  紧凑型 CPU(`6ES7 511-1CK00-0AB0/V2.9`)→ DI16(`6ES7 521-1BH00`)+ DQ16(`6ES7 522-1BH00`)→ 配 IP / 掩码 / 子网 / IO-System。
- **GSD 从站(真实项目 IO 的主力)**:很多机台的 IO **不在 CPU 机架,而是 PROFINET IO 从站(GSD 设备)**——耦合器 + DI/DQ/IO-LINK/伺服/阀岛,全挂在 PROFINET IO-System 上;CPU 做 IO 控制器,从站连到 IO-System。`tia_device_add` 用 `GSD:` 开头的 typeId(缺 head 后缀会自动回退 `/DAP` `/D` `/SM` `/M`),从站里的子模块同理。用 `tia_hardware_read` 看全貌(设备/子模块/子网节点 IP/IO-System 连了谁)。
- **⚠️ `PnDeviceName` setter V21 不支持**:`tia_network_configure` 传 `pnDeviceName` 会被透明跳过(返回里标 skipped),不是 bug,是 V21 限制。
- `tia_hardware_read` 的 `nodes`/`ioSystems` 能读到子网下的节点(含 IP)与 IO-System 的连接设备。

## 数据驱动(IO 点表 → tag)

真实项目按 **IO 点表**(Excel)组织地址分配。点表给出每个 IO 点的 **地址 + 注释**(如 `I100.0` / `OP010设备上电`),用 `tia_tag_create` 建成 PLC tag(名=地址、地址=`%I100.0`、注释=描述、Bool)。

**读 xlsx**(本机装了 Excel):用 Excel COM 读点表 → 抽出 DI/DQ/IO-LINK 的(地址, 注释)对 → 逐个 `tia_tag_create`。点表的列布局因项目而异(典型:DI 地址/注释、DQ 地址/注释、IO-LINK 地址/注释各占两列,模块说明行夹在中间),按你公司的点表格式调整列号。
```powershell
$excel = New-Object -ComObject Excel.Application; $excel.Visible=$false
$v = $excel.Workbooks.Open("...\<your-io-table>.xlsx").Sheets.Item("<sheet>").UsedRange.Value2
# 第 r 行:DI 地址=$v[$r,2] 注释=$v[$r,3];DQ 地址=$v[$r,5] 注释=$v[$r,6];IO-LINK 地址=$v[$r,8] 注释=$v[$r,9]
# → 遍历行,地址非空且非"备用"的,调 tia_tag_create
```
→ 拿到 (addr, comment) 后:`tia_tag_create(name=addr, address="%"+addr, dataType="Bool", comment)`。只建**实质点**(非"备用"),备用等用到再补。

⚠️ **IO tag 不依赖物理模块/从站** —— 地址先占住,块就能编译引用;物理 PROFINET 从站(让 tag 有真实驱动)是运行时事(见上「GSD 从站」)。点表格式 + 地址方案见 [`examples/io-tables/`](examples/io-tables/) + [`命名规范`](../_reference/naming.md)。

## 校验

`tia_project_compile`(scope=项目或 PLC)→ Hardware **0/0**;`tia_hardware_read` 能列出刚加的设备/模块/IP。

## 常见报错 → 修法

| 报错 | 原因 / 修法 |
|------|-------------|
| 找不到 typeIdentifier / add 失败 | 先 `tia_catalog_search` 拿精确 id;GSD 缺后缀靠自动回退 |
| 模块插不进 | 槽位被占;省略 `slot` 让它自动找空槽 |
| pnDeviceName 没生效 | V21 不支持该 setter,预期行为 |

## 状态

已实测:catalog_search、device_add、module_add、network_configure(IP+掩码+子网+IO-System)、hardware_read(含 PROFINET GSD 从站 + 子模块 + IO-System 连接)、硬件编译 0/0、device/module/subnet delete、**IO 点表(xlsx,Excel COM)→ 批量 tia_tag_create**。

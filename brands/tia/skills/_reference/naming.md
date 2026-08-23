# 命名规范 & 地址方案

> 何时用:组态/编程时给设备、网络、块、地址起名,保持与团队/参考工程一致。下面是一套常见的两层命名约定(可复用设备库块 vs 站点实例),按你公司的规范调整。

## 程序分层(块命名)

- **`G_*` —— 可复用设备库块**:与工位无关的通用设备逻辑(伺服/气缸/阀/IO 监控等),通常来自公司标准全局库。
  - 例:`G_FB<NNN>_<DeviceType>`(如某个伺服、IO 监控设备块)。
- **`OP<NN>_*` —— 站点(工位)实例**:某工位对 `G_*` 的实例化 + 接线。
  - 例:`OP<NN>_<Station>`(按工位号 + 站点语义命名)。
- **`OP<NN>_Call` —— 工位调用 FC**:统一编排本工位所有实例,再被循环 OB(Main/OB1)调用。

> 规律:块名在 TIA 里**全局唯一**,所以按名递归查找无歧义(`tia_block_list` 已带组路径)。

## 块/类型/变量的文件夹组织

- 块组:常见多层分级(如 `<Global>/<Category>/<Device>/SubBlocks`),按公司规范组织;`tia_block_list` 已带组路径。
- 类型组:UDT 按类别分子组(如 `<TypeRoot>/<SubCategory>`)。
- 用 `tia_group_create(kind=block|type|tagtable)` 建组。

## 网络命名

| 网络类型 | 命名例 |
|----------|--------|
| PROFINET | `PN/IE_1`、`PN/IE_100`、`PN/IE_2` |
| PROFIBUS | `PROFIBUS_1`、`PROFIBUS_100` |
| Ethernet | `Ethernet_100` |
| AS-i | `AS-i_100` |

## 地址 / 槽位方案

- **槽位**:0 = 电源(PM),1 = CPU/基座,2+ = 可插模块。`tia_module_add` 省略 slot 自动找空槽(≥2)。
- **IO 地址**:整数字节偏移(0、100、110、200、300…)。模块位置可简单(2/3/4)或嵌套(`10X1` = 槽 10 位 1)。
- **IP(PROFINET)**:`192.168.X.Y`,掩码 `255.255.255.0`。
- **PROFIBUS 站地址**:小整数(1–54)。

## 设备命名

- 本地 CPU:`S71200` / `S71500`(`tia_device_add` 里 deviceName 给 CPU 项名,站会被命名 `PLC_1`)。
- 远程从站:`ET200SP` / `ET200Pro` / `ET200ECO` 等;按 `NewDevice1_ET200SP_PN` 之类带网络后缀。

## 维护
TODO:沉淀更细的工位/设备命名字典与地址段划分表(配合 [`../../examples/io-tables/`](../../examples/io-tables/))——按你公司的规范填充。

# MCP 工具清单(51 个)

> 何时用:需要查某个能力对应哪个 MCP 工具、要什么模式时。按能力分组。模式以实际 AccessGuard 为准;**读类工具任意模式可调**。

模式图例:`读`=只读(任意模式)/ `写`=需 ReadWrite / `删`=需 ReadWrite + `confirm=true` / `危`=需 Unrestricted + `confirm=true`。

## 会话 / 系统
| 工具 | 模式 | 用途 |
|------|------|------|
| `tia_status` | 读 | server+backend 状态、当前生效模式 |
| `tia_connect` | 读 | 连接/挂接 TIA(attach/interactive/headless),返回会话与项目名 |
| `tia_disconnect` | 读 | 释放 TIA 会话(关项目 + Dispose Portal,worker 留着,下次 connect 重起) |

## 项目
| 工具 | 模式 | 用途 |
|------|------|------|
| `tia_project_open` | 读 | 打开项目(`.ap21`;旧 `.ap18/.ap19` 会被 TIA 升级重组后另存为 `.ap21`)。`visible=false` 走无界面打开;路径会归一化为绝对 |
| `tia_project_list` | 读 | 列项目内设备目标(PLC/HMI) |
| `tia_project_status` | 读 | 项目元信息 |
| `tia_project_compile` | 写 | 编译(Software/Hardware/All),协调块版本并写盘,需 ReadWrite;返回结构化诊断 |
| `tia_project_save` | 写 | 保存 |
| `tia_project_save_as` | 写 | 另存(会重绑当前项目) |
| `tia_project_create` | 写 | 新建项目 |
| `tia_project_archive` | 写 | 归档 |
| `tia_project_close` | 写 | 关闭 |

## 块(读)
| 工具 | 模式 | 用途 |
|------|------|------|
| `tia_block_list` | 读 | 列块(含所在用户组路径),分页 |
| `tia_block_info` | 读 | 块头(名/类型/号/语言/注释) |
| `tia_block_read_code` | 读 | 块体紧凑结构化视图(LAD=表达式+线圈+盒清单 / SCL=拍平文本 / GRAPH=步转移;支持网络区间过滤) |
| `tia_block_read_source` | 读 | 读块 SimaticML 源(不一致块可能触发一次恢复编译,写盘) |
| `tia_interface_read` | 读 | 块/UDT 结构化接口成员树(不一致块可能触发一次恢复编译,写盘) |
| `tia_udt_list` | 读 | 列 PLC 数据类型(UDT),含组路径 |
| `tia_cross_reference` | 读 | 交叉引用(被谁用/用了谁) |
| `tia_block_export` | 读 | 导出 **block 或 UDT**(Xml / SclSource;UDT 仅 Xml)到磁盘 |

## 块(写)
| 工具 | 模式 | 用途 |
|------|------|------|
| `tia_block_import` | 写 | 导入 SimaticML XML 块(`source` 可为文件路径;`plcPath` 带 `/blockgroup:NAME` 导入到子组) |
| `tia_block_write_code` | 写 | 结构化 JSON spec → SimaticML → 导入(LAD 标准集;Override 幂等;dryRun 只出 XML) |
| `tia_block_generate_from_source` | 写 | 喂 SCL/AWL 文本生成块 |
| `tia_block_create_from_copy` | 写 | 从库母版实例化块 |
| `tia_block_delete` | 删 | 删 **block 或 UDT** |

## 变量 / 数据类型
| 工具 | 模式 | 用途 |
|------|------|------|
| `tia_tag_list` | 读 | 列 tags |
| `tia_tagtable_list` | 读 | 列变量表(含 tag 数、组路径) |
| `tia_tagtable_export` | 读 | 导出整张变量表(含其 tags)为 SimaticML XML → `<TableName>.xml` |
| `tia_tag_create` | 写 | 建 tag |
| `tia_tagtable_import` | 写 | 导入变量表 XML |
| `tia_udt_import` | 写 | 导入 UDT(`sourceXml` 可为文件路径;`plcPath` 带 `/typegroup:NAME` 导入到子组,=移动) |
| `tia_tag_delete` | 删 | 删 tag |
| `tia_group_create` | 写 | 建组/文件夹(block/type/tagtable) |

## 硬件 / 网络
| 工具 | 模式 | 用途 |
|------|------|------|
| `tia_catalog_search` | 读 | 搜硬件目录拿 typeIdentifier |
| `tia_hardware_read` | 读 | 读硬件配置(设备/子网/IO-System/节点) |
| `tia_device_item_list` | 读 | 列某设备的机架项(模块/槽) |
| `tia_device_add` | 写 | 从目录建设备(CPU) |
| `tia_module_add` | 写 | 往机架插模块 |
| `tia_network_configure` | 写 | 配 IP/掩码/子网/IO-System |
| `tia_cpu_system_clock_memory` | 写 | 配 CPU 系统/时钟存储字节(启用后 TIA 自动建 FirstScan/AlwaysTRUE/Clock_* tag) |
| `tia_device_delete` | 删 | 删设备/站 |
| `tia_module_delete` | 删 | 删模块 |
| `tia_subnet_delete` | 删 | 删子网 |

## 库复用
| 工具 | 模式 | 用途 |
|------|------|------|
| `tia_library_open` | 写 | 打开全局库 `.al21`(已开则复用) |
| `tia_mastercopy_list` | 读 | 列库内母版 |

> `tia_block_create_from_copy` 见「块(写)」。

## 在线 / 真实硬件
| 工具 | 模式 | 用途 |
|------|------|------|
| `tia_online_status` | 读 | 在线状态(⚠️ V21 真实后端无此 API,返回 Unknown) |
| `tia_online_connect` | 写 | 建在线连接 |
| `tia_online_disconnect` | 写 | 断在线连接 |
| `tia_download` | 危 | 下载到 PLC(唯一真实在线动作) |
| `tia_plc_run` | 危 | RUN(⚠️ V21 Openness 不支持,仅 Fake 模拟) |
| `tia_plc_stop` | 危 | STOP(⚠️ 同上) |

> ⚠️ V21 Openness **没有** go-online / CPU RUN-STOP API:`online_*`/`plc_run`/`plc_stop` 在真实后端返回 NotSupported(只在 Fake 模拟),`download` 是唯一真实在线动作。详见 [`安全与模式`](safety-modes.md)。

## 维护提示
新增/改名工具后,本清单要与 `brands/tia/mcp/src/TiaMcp.Server/Tools/*.cs` 的 `[McpServerTool(Name=...)]` 保持一致(当前 51 个)。

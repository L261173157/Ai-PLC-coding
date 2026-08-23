# 示例:只读通读一个真实项目

> 何时用:想"看懂一个现成 PLC 工程"——纯只读,任意模式可跑,不改任何东西。这是熟悉一个陌生项目的标准动作。

对象:任意一个你想读懂的现成工程(下面以 `<your-project>` 代指)。把它换成你手上项目的实际路径。

## 步骤

前置:`tia_connect`(attach 到已开的 GUI,或 headless)+ `tia_project_open` 打开该项目。PLC 路径形如 `session:s-openness/project:<name>/plc:program`。

1. **看硬件** `tia_hardware_read` → 设备 / 子网 / IO-System / 节点(含 IP)。
   - 据此画出硬件拓扑:几个 CPU、哪些 PROFINET/PROFIBUS、挂了哪些 IO 从站(GSD 设备)。
2. **列块** `tia_block_list`(limit 拉大)→ 看程序骨架 + 每块所在的**用户组路径**。
   - 据此判断程序分层:可复用库块通常在一个全局组下,工位实例块在按工位/类别分的子组里。
3. **列数据类型** `tia_udt_list` → UDT 全貌(按组归类)。
4. **列变量表** `tia_tagtable_list` → 变量表 + 每张表的 tag 数。
5. **挑块深入**(读懂具体逻辑时):
   - `tia_interface_read <某块>` → 结构化接口(分段 → 成员,带类型/初值/注释)。
   - `tia_cross_reference <某块>` → 它被谁用 / 用了谁(评估改动影响面)。
   - `tia_block_read_source <某块>` → 读 SimaticML 源。

## 产出

一份"这个项目有什么设备、什么程序结构、关键块怎么连"的画像,作为后续修改的基础。

## 自动化:一键通读脚本

不想手动逐个调?`brands/tia/mcp/tests/read_full_project.py` 把上面整条链跑一遍,把每个结果落成 JSON:

```
python brands/tia/mcp/tests/read_full_project.py <server.dll> <项目.ap21>
# 输出:brands/tia/mcp/tests/output/<项目名>/ (hardware.json、devices/<PLC>/blocks/…、summary.json)
```

- 跑前先把项目**复制一份**到 `plc/_scratch/`,读副本(读块时可能触发恢复编译、翻转 isModified,别污染原件)。
- 脚本逐 PLC 分页 `tia_block_list`(limit 500),每块再 `info/interface/xref/source`,**每调用各自 try/except**——单块失败(如校验不一致)不会拖垮整轮。
- 配套 `write_demo_project.py`(克隆现有块为新块,验证写入闭环)、`verify_fixes.py`/`verify_live_fixes.py`(离线/真机回归)。

## 已知注意

- **设备都在组里**:`tia_project_list` / 设备路径解析会递归 `UngroupedDevicesGroup`/设备组——真实项目常把 PLC 站和 IO 从站都放组里(非顶层 `project.Devices`)。若 `tia_project_list` 返回空、或某设备"按路径找不到",说明在用旧版 worker(2026-07 修复过该递归),重编 worker(它不在 `TiaMcp.slnx` 里,要单独 `dotnet build ...Worker.csproj`)。
- **CPU 系统/时钟内存读**:S7-1200 上 Openness 不暴露这些属性(`EngineeringNotSupportedException`),工具会明确报出;S7-1500 正常。

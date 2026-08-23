# IO 点表(把点表喂给 MCP 工具)

> 何时用:做硬件组态/地址分配时,手上有一份 IO 点表(Excel),想据此批量组态设备、模块、地址、建 tag。

## 思路

IO 点表通常给出每个 IO 点的 **地址 + 注释**(如 `I0.0` / `设备上电`),按设备/模块分组。把它喂给 MCP 工具有两条路:

- **建 tag**(地址 + 注释):`tia_tag_create(name=地址, address="%"+地址, dataType="Bool", comment=注释)` 逐点建;或整表导出成 SimaticML XML 后 `tia_tagtable_import` 批量导入。
- **组态硬件**(设备 → 模块 → 端口 → 地址):按点表的设备/模块,配合 [硬件组态](../../SKILL.md) 的工作流,逐项 `tia_device_add` / `tia_module_add` / `tia_network_configure`。

## 读 xlsx(本机装了 Excel)

用 Excel COM 读点表,抽出 DI/DQ/IO-LINK 的 (地址, 注释) 对,再逐个 `tia_tag_create`。点表的列布局因项目而异(典型:DI 地址/注释、DQ 地址/注释、IO-LINK 地址/注释各占两列,模块说明行夹在中间),按你公司的点表格式调整列号。

```powershell
$excel = New-Object -ComObject Excel.Application; $excel.Visible=$false
$v = $excel.Workbooks.Open("...\<your-io-table>.xlsx").Sheets.Item("<sheet>").UsedRange.Value2
# 第 r 行:DI 地址=$v[$r,2] 注释=$v[$r,3];DQ 地址=$v[$r,5] 注释=$v[$r,6];IO-LINK 地址=$v[$r,8] 注释=$v[$r,9]
# → 遍历行,地址非空且非"备用"的,调 tia_tag_create
```

地址方案见 [命名规范](../../../_reference/naming.md)。

## TODO

沉淀一份「点表列 → MCP 调用参数」的字段映射表(等你确定点表标准列头后补)。

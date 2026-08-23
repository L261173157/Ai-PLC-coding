# 用法: strip-cultures.ps1 -In <块.xml> -Out <块.zhcn.xml>
# 去掉 ObjectList 里的 <MultilingualTextItem>(块注释/标题,目标项目缺这些 culture 会导入失败)
param([Parameter(Mandatory)][string]$In, [Parameter(Mandatory)][string]$Out)
$x = New-Object System.Xml.XmlDocument
$x.PreserveWhitespace = $true
$x.Load($In)
foreach ($m in @($x.SelectNodes("//*[local-name()='MultilingualTextItem']"))) {
  $m.ParentNode.RemoveChild($m) | Out-Null
}
$x.Save($Out)
Write-Output ("wrote {0} ({1} bytes)" -f $Out, (Get-Item $Out).Length)

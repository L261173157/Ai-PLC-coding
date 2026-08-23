# 用法: merge.ps1 -Dir <UDT的xml目录> -Out <批名> -Names "TypeA,TypeB,TypeC"
# 输出 <Dir>/_merged_<Out>.xml,内含这批 UDT,依赖在前,最小化(剥 ObjectList+BooleanAttribute,重编 ID)
param(
  [Parameter(Mandatory)][string]$Dir,
  [Parameter(Mandatory)][string]$Out,
  [Parameter(Mandatory)][string]$Names
)
$list = $Names -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ }
$set = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($n in $list) { $null = $set.Add($n) }

# 解析每个类型的带引号类型引用("&quot;OtherType&quot;"),只保留本批内部的
$deps = @{}
foreach ($n in $list) {
  $p = Join-Path $Dir "$n.xml"
  if (-not (Test-Path $p)) { $deps[$n] = @(); continue }
  $content = Get-Content $p -Raw
  $refs = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
  foreach ($m in [regex]::Matches($content, '&quot;([A-Za-z0-9_]+)&quot;')) {
    if ($set.Contains($m.Groups[1].Value)) { $null = $refs.Add($m.Groups[1].Value) }
  }
  $deps[$n] = $refs
}

# 拓扑排序:反复挑"依赖已就位"的类型
$placed = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$order = @(); $remaining = @($list)
while ($remaining.Count -gt 0) {
  $next = @(); $progress = $false
  foreach ($n in $remaining) {
    $ready = $true
    foreach ($d in $deps[$n]) { if (-not $placed.Contains($d)) { $ready = $false; break } }
    if ($ready) { $order += $n; $null = $placed.Add($n); $progress = $true } else { $next += $n }
  }
  $remaining = $next
  if (-not $progress) { foreach ($n in $remaining) { $order += $n }; break }  # 环:剩余按原序追加
}

# 按拓扑序合并(最小形态,重编 ID)
$merged = New-Object System.Xml.XmlDocument; $merged.PreserveWhitespace = $true
$merged.LoadXml("<?xml version='1.0' encoding='utf-8'?><Document><Engineering version='V21' /></Document>")
$docEl = $merged.DocumentElement; $offset = 0; $missing = @()
foreach ($n in $order) {
  $p = Join-Path $Dir "$n.xml"
  if (-not (Test-Path $p)) { $missing += $n; continue }
  $x = New-Object System.Xml.XmlDocument; $x.PreserveWhitespace = $true; $x.Load($p)
  $struct = $x.SelectSingleNode("//*[local-name()='SW.Types.PlcStruct']")
  $ol = $struct.SelectSingleNode("*[local-name()='ObjectList']"); if ($ol) { $struct.RemoveChild($ol) | Out-Null }
  foreach ($m in $struct.SelectNodes("//*[local-name()='Member']")) {
    $ma = $m.SelectSingleNode("*[local-name()='AttributeList']"); if ($ma) { $m.RemoveChild($ma) | Out-Null }
  }
  foreach ($el in $struct.SelectNodes("descendant-or-self::*")) {
    $a = $el.Attributes['ID']; if ($a) { $a.Value = ([convert]::ToInt32($a.Value) + $offset).ToString() }
  }
  $docEl.AppendChild($merged.ImportNode($struct, $true)) | Out-Null
  $offset += 1000
}
$outFile = Join-Path $Dir "_merged_$Out.xml"; $merged.Save($outFile)
Write-Output ("wrote {0} ({1} bytes, {2} types)" -f $outFile, (Get-Item $outFile).Length, $order.Count)
Write-Output ("order: " + ($order -join ' -> '))
if ($missing) { Write-Output ("MISSING: " + ($missing -join ', ')) }

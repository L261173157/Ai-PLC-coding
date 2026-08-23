#requires -Version 5.1
<#
.SYNOPSIS
  把品牌 skills / 通用 skills 同步进 Claude Code 插件目录(dev 就地加载)。
.DESCRIPTION
  plugins/<name>/skills/ 是派生产物(gitignored;见根 .gitignore)——可从以下源头重建:
    brands/tia/skills/  ->  plugins/plc-siemens/skills/
    skills/             ->  plugins/plc-base/skills/
  改完 skills、在 `claude --plugin-dir plugins/<name>` 加载插件前跑一次。增量覆盖(从不删除);.gitkeep 保留。
.EXAMPLE
  ./scripts/dev-sync-plugins.ps1
#>
param(
  [string]$RepoRoot = (Resolve-Path "$PSScriptRoot\..").Path
)
$ErrorActionPreference = 'Stop'

$pairs = @(
  @{ Src = Join-Path $RepoRoot 'brands\tia\skills'; Dst = Join-Path $RepoRoot 'plugins\plc-siemens\skills'; Label = 'plc-siemens <- brands/tia/skills' }
  @{ Src = Join-Path $RepoRoot 'skills';            Dst = Join-Path $RepoRoot 'plugins\plc-base\skills';   Label = 'plc-base    <- skills (generic)' }
)

foreach ($p in $pairs) {
  if (-not (Test-Path -LiteralPath $p.Src)) {
    Write-Warning "跳过 $($p.Label):源不存在 ($($p.Src))"
    continue
  }
  New-Item -ItemType Directory -Force -Path $p.Dst | Out-Null
  # /E = 复制子目录(含空)。无 /MIR => 增量覆盖、从不删除。.gitkeep 保留。
  robocopy $p.Src $p.Dst /E /NFL /NDL /NJH /NJS /NP | Out-Null
  if ($LASTEXITCODE -ge 8) {
    throw "robocopy 失败(退出码 $LASTEXITCODE):$($p.Src) -> $($p.Dst)"
  }
  $global:LASTEXITCODE = 0
  $n = @(Get-ChildItem -Recurse -File -LiteralPath $p.Dst -ErrorAction SilentlyContinue).Count
  Write-Host "OK  $($p.Label)  ($n 个文件)"
}

Write-Host "完成。插件 skills/ 为派生产物(gitignored)。改完 skills 重跑。"

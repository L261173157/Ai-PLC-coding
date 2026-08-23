#requires -Version 5.1
<#
.SYNOPSIS
  把 build-plugin.ps1 产出的自包含插件包成 Claude Code marketplace 可分发的 zip。
.DESCRIPTION
  Claude Code 的 marketplace **不能是单个 zip**——它必须是一个带
  .claude-plugin/marketplace.json 的目录(或 git 仓库 / URL)。本脚本把
  dist/plugins/plc-siemens/(自包含插件)拢进一个市场根目录,再压成 zip。
  消费者解压后用本地路径 add(Claude Code CLI,在终端跑,**不是** /plugin 斜杠命令):

    claude plugin marketplace add <解压出的 tia-marketplace 目录>
    claude plugin install tia-mcp@tia-plugins
    # 重启 Claude Code 生效(install/update 需重启应用)

  升级(覆盖旧版——市场目录用版本无关路径,用新 build 覆盖其内容后):
    claude plugin marketplace update tia-plugins
    claude plugin update tia-mcp@tia-plugins
    # 重启 Claude Code

  产物(都在 dist/ 下):
    tia-marketplace/                         市场根(可直接本地 add,不必 zip)
      .claude-plugin/marketplace.json        市场清单(owner/description 取自 plugin.json)
      使用说明.txt                            消费者安装/升级说明(也进 zip)
      plc-siemens/                           = dist/plugins/plc-siemens
    tia-plugins-<version>.zip                解压即得市场根(zip 内容,无顶层目录,含使用说明.txt)

  版本缺省取自插件 plugin.json(单一事实源);可用 -Version 覆盖(仅影响 zip 文件名)。
  先跑 ./scripts/build-plugin.ps1 -Version <v> 产出 dist/plugins/plc-siemens/。
.EXAMPLE
  ./scripts/package-marketplace.ps1
  ./scripts/package-marketplace.ps1 -Version 0.2.0
#>
param(
  [string]$Version,                                                    # 缺省取自 plugin.json
  [string]$MarketplaceName = 'tia-plugins',
  [string]$PluginDir = (Join-Path (Resolve-Path "$PSScriptRoot\..").Path 'dist\plugins\plc-siemens'),
  [string]$Dist         = (Join-Path (Resolve-Path "$PSScriptRoot\..").Path 'dist'),
  [string]$RepoRoot     = (Resolve-Path "$PSScriptRoot\..").Path
)
$ErrorActionPreference = 'Stop'

$pluginJsonPath = Join-Path $PluginDir '.claude-plugin\plugin.json'
if (-not (Test-Path $pluginJsonPath)) {
  throw "找不到自包含插件清单 $pluginJsonPath。先跑 ./scripts/build-plugin.ps1 -Version <v>。"
}
$plugin = Get-Content $pluginJsonPath -Raw | ConvertFrom-Json
if (-not $Version) { $Version = $plugin.version }
if (-not $Version) { throw "无法确定版本:plugin.json 没有 version,也没传 -Version。" }

$marketRoot = Join-Path $Dist 'tia-marketplace'
$pluginDest = Join-Path $marketRoot 'plc-siemens'   # 目录名须与下方 source 一致
$zip        = Join-Path $Dist "tia-plugins-$Version.zip"

# 1. 写 marketplace.json(owner/description 取自插件 plugin.json,保持同步)。
if (Test-Path $marketRoot) { Remove-Item $marketRoot -Recurse -Force }
New-Item -ItemType Directory -Force -Path "$marketRoot\.claude-plugin" | Out-Null
$catalog = [ordered]@{
  name    = $MarketplaceName
  owner   = [ordered]@{ name = $plugin.author.name; url = $plugin.author.url }
  plugins = @(
    [ordered]@{
      name        = $plugin.name                 # "tia-mcp"
      source      = './plc-siemens'              # 市场根内的相对路径(须以 ./ 开头)
      description = $plugin.description
    }
  )
}
[IO.File]::WriteAllText(
  (Join-Path $marketRoot '.claude-plugin\marketplace.json'),
  ($catalog | ConvertTo-Json -Depth 8),
  [Text.UTF8Encoding]::new($false))

# 1b. 消费者使用说明(中文,放进市场根 -> 自动进 zip;直接用目录的人也看得到)。
#     UTF-8 带 BOM,确保旧版记事本能正确显示中文。
$readme = @'
================================================================
  tia-mcp 插件使用说明(西门子 TIA Portal V21 · MCP)
================================================================

【这是什么】
本目录是 Claude Code 的"市场"(marketplace),含插件 tia-mcp
(TIA Portal 的 MCP server + TIA 工程技能)。解压后,"当前这个
文件夹"就是市场根,直接用它即可。

【电脑前置 · 必须】
  1) Windows + TIA Portal V21(安装时勾选 Openness)
  2) 当前 Windows 用户加入本地组 "Siemens TIA Openness"(加完注销重登)
  无需 .NET SDK、无需自己编译 —— 插件已自包含,worker 会从你本机
  的 TIA 自动加载 Siemens DLL。

【首次安装】
  在 PowerShell / 终端跑(不是 Claude 里的斜杠命令):

  claude plugin marketplace add "<本目录的完整路径>"
  claude plugin install tia-mcp@tia-plugins
  # 然后重启 Claude Code

【升级(新版本已覆盖到本目录后)】
  claude plugin marketplace update tia-plugins
  claude plugin update tia-mcp@tia-plugins
  # 然后重启 Claude Code

【常见问题】
  · "not installed at scope user" → 装在别的 scope。跑
    claude plugin list --json 看 scope,再 install/update 加 -s project。
  · 装完没反应 / 仍是旧版 → install/update 后必须重启 Claude Code。
  · Claude Code 没有 /plugin install 这类斜杠命令;上面是终端命令
    (REPL 里 /plugin 仅交互点选)。
  · 升级靠 plugin.json 的 version 比对;不升版本号会被误判"已是最新"。

【验证】重启后,在 Claude Code 里直接操作 TIA 项目;或让它跑
  tia_status 确认在线(应显示 Openness / V21 / ReadWrite)。
================================================================
'@
[IO.File]::WriteAllText(
  (Join-Path $marketRoot '使用说明.txt'),
  $readme, [Text.UTF8Encoding]::new($true))

# 2. 把自包含插件拷进市场根。
Write-Host "==> 组装 marketplace -> $marketRoot"
Copy-Item $PluginDir $pluginDest -Recurse -Force

# 3. 压成 zip:压 marketRoot 的*内容*(不带 tia-marketplace\ 顶层目录)。这样解压器生成的
#    文件夹(通常以 zip 名命名)直接就是市场根,消费者 marketplace add <解压目录> 即可。
#    若带顶层目录,会和 Windows "解压全部" 等解压器再套一层,造成双重嵌套、add 找不到。
Add-Type -AssemblyName System.IO.Compression.FileSystem
if (Test-Path $zip) { Remove-Item $zip -Force }
[System.IO.Compression.ZipFile]::CreateFromDirectory(
  $marketRoot, $zip,
  [System.IO.Compression.CompressionLevel]::Optimal, $false)

$size  = [math]::Round((Get-ChildItem $marketRoot -Recurse -File | Measure-Object Length -Sum).Sum / 1MB, 1)
$zipMB = [math]::Round((Get-Item $zip).Length / 1MB, 1)
Write-Host "==> OK"
Write-Host "  市场目录: $marketRoot  (~$size MB,可直接本地 add)"
Write-Host "  分发 zip: $zip  (~$zipMB MB)"
Write-Host "  消费者首次安装(解压出的目录即市场根;Claude Code CLI,在终端跑,非 /plugin 斜杠命令):"
Write-Host "    claude plugin marketplace add <解压出的目录>"
Write-Host "    claude plugin install $($plugin.name)@$MarketplaceName"
Write-Host "    # 然后重启 Claude Code(plugin install/update 需重启应用)"
Write-Host "  消费者升级(把新 build 覆盖到已登记的市场目录后):"
Write-Host "    claude plugin marketplace update $MarketplaceName"
Write-Host "    claude plugin update $($plugin.name)@$MarketplaceName"
Write-Host "    # 然后重启 Claude Code"
Write-Host "  注:命令在终端跑;没有 /plugin install、/plugin update 斜杠命令。scope 默认 user,装在项目级加 -s project(查 scope: claude plugin list --json)。"
Write-Host "  消费者机器仍需:TIA Portal V21 + 用户在 'Siemens TIA Openness' 组(无需 .NET SDK、无需自 build worker)"

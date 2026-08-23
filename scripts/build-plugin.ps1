#requires -Version 5.1
<#
.SYNOPSIS
  为西门子 TIA 品牌组装自包含的 Claude Code 插件,并(默认)打包成可分发的 marketplace。
.DESCRIPTION
  产出 dist/plugins/plc-siemens/ —— 自包含插件,消费者机器上无需 .NET SDK(区别于 dev 版
  .mcp.json 用 `dotnet run` 从源码跑)。默认再跑 package-marketplace.ps1 把它拢进市场根
  dist/tia-marketplace/ 并压成 dist/tia-plugins-<v>.zip(⚠️ zip 不能直接当 marketplace;
  Claude Code 的 marketplace 必须是带 marketplace.json 的目录/git 仓库/URL):

    dist/plugins/plc-siemens/
      .claude-plugin/plugin.json     (复制源 + version 同步成 -Version,消除与程序集版本漂移)
      .mcp.json                       (PUBLISH 模式:跑打包进来的 ./server/TiaMcp.Server.exe)
      server/                         (win-x64 self-contained publish + openness-worker/)
      skills/                         (自 brands/tia/skills/ 复制)

  流水线:构建 net48 worker -> 发布 self-contained server(BundleOpennessWorker 把 worker
  折进 server\openness-worker\)-> 塑成插件目录(plugin.json 打版本 + 写 publish 模式 .mcp.json)
  -> (默认)打包成 marketplace。

  与 build-installer.ps1 同前置:在 TIA V21 机器上跑,需 .NET Framework 4.8 dev pack、
  .NET 10 SDK,且当前用户在 'Siemens TIA Openness' 本地组。

  BundleOpennessWorker(TiaMcp.Server.csproj 的 AfterPublish)在 worker 未预构建时硬失败——
  故第 1 步是强制的,除非 -NoWorker(本次会话已构建过 worker)。
.EXAMPLE
  ./scripts/build-plugin.ps1                              # 版本自动取自源 plugin.json(改版本只改它)
  ./scripts/build-plugin.ps1 -Version 0.3.0               # 显式指定(覆盖)
  ./scripts/build-plugin.ps1 -NoMarketplace               # 只出插件目录,不打包 marketplace
  ./scripts/build-plugin.ps1 -NoWorker                    # worker 已构建,跳过 worker 构建
#>
param(
  [string]$Version,                 # 省略时自动取源 plugin.json 的 version(单一事实源);传 -Version 可覆盖
  [string]$Dist     = "$PSScriptRoot\..\dist",
  [string]$RepoRoot = (Resolve-Path "$PSScriptRoot\..").Path,
  [switch]$NoWorker,
  [switch]$NoMarketplace   # 跳过打包成 marketplace(目录 + zip)
)
$ErrorActionPreference = 'Stop'
# 版本号缺省:从源 plugin.json 读(单一事实源——改版本只改 plugins/plc-siemens/.claude-plugin/plugin.json)。
# 这样发版流程是:先在源 plugin.json 改 version,再无参 ./scripts/build-plugin.ps1;传 -Version 可临时覆盖。
if (-not $Version) {
  $srcManifest = Join-Path $RepoRoot 'plugins\plc-siemens\.claude-plugin\plugin.json'
  if (-not (Test-Path $srcManifest)) { throw "未传 -Version,且找不到源 manifest: $srcManifest" }
  $Version = (Get-Content $srcManifest -Raw | ConvertFrom-Json).version
  Write-Host "==> 版本号取自源 manifest ($srcManifest): $Version  (传 -Version 可覆盖)"
}
# 规整 Version:去掉常见的 'v'/'V' 前缀,并校验为 X.Y.Z(NuGet 版本字符串不接受 'v1.0',
# 且 AssemblyVersion/FileVersion 必须是 4 段纯数字)。Version(InformationalVersion)保持原样。
$Version = "$Version".TrimStart('vV')
if ($Version -notmatch '^\d+\.\d+\.\d+$') {
  throw "Version 必须是 'X.Y.Z' 格式(如 0.2.0),不要带 'v' 前缀。收到: '$Version'"
}
$asm = "$Version.0"   # 0.2.0 -> 0.2.0.0

$mcp       = Join-Path $RepoRoot 'brands\tia\mcp'
$serverPub = Join-Path $Dist 'TiaMcp'               # self-contained server 发布(与安装包同形状)
$pluginOut = Join-Path $Dist 'plugins\plc-siemens'  # 组装出的插件目录
$skillsSrc = Join-Path $RepoRoot 'brands\tia\skills'
$pluginSrc = Join-Path $RepoRoot 'plugins\plc-siemens'  # 源 manifest + dev .mcp.json

# 1. 构建 net48 Openness worker(仅 TIA V21 机器)。
if (-not $NoWorker) {
  Write-Host "==> 构建 net48 worker"
  dotnet build "$mcp\src\TiaMcp.Openness.Worker\TiaMcp.Openness.Worker.csproj" -c Release `
    "-p:Version=$Version" "-p:FileVersion=$asm" "-p:AssemblyVersion=$asm"
  if ($LASTEXITCODE -ne 0) { throw "net48 worker 构建失败(dotnet exit $LASTEXITCODE)。" }
}

# 2. 发布 self-contained server。BundleOpennessWorker(AfterPublish)把 worker 拷进
#    <publish>\openness-worker\ —— server + worker 作为一个文件夹发布。
Write-Host "==> 发布 self-contained server(win-x64)"
if (Test-Path $serverPub) { Remove-Item $serverPub -Recurse -Force }
dotnet publish "$mcp\src\TiaMcp.Server\TiaMcp.Server.csproj" -c Release `
  -r win-x64 --self-contained true -o $serverPub `
  "-p:Version=$Version" "-p:FileVersion=$asm" "-p:AssemblyVersion=$asm"
if ($LASTEXITCODE -ne 0) { throw "self-contained server 发布失败(dotnet exit $LASTEXITCODE)。" }
Get-ChildItem $serverPub -Recurse -Filter *.pdb -ErrorAction SilentlyContinue | Remove-Item -Force

# 3. 组装插件目录。
Write-Host "==> 组装插件 -> $pluginOut"
if (Test-Path $pluginOut) { Remove-Item $pluginOut -Recurse -Force }
New-Item -ItemType Directory -Force -Path "$pluginOut\.claude-plugin" | Out-Null

# 3a. 插件 manifest:复制源,并把 version 同步成 -Version(消除 plugin.json 与程序集版本漂移)。
$manifest = Get-Content "$pluginSrc\.claude-plugin\plugin.json" -Raw | ConvertFrom-Json
$manifest.version = $Version
[IO.File]::WriteAllText("$pluginOut\.claude-plugin\plugin.json",
  ($manifest | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))

# 3b. PUBLISH 模式 .mcp.json:跑打包进来的 exe(不是 `dotnet run` 从源码)。
#     单引号 here-string 使 ${CLAUDE_PLUGIN_ROOT} 保持字面量(不被 PS 插值);
#     以 UTF-8 无 BOM 写入,与已提交的 dev .mcp.json 一致。
$mcpJson = @'
{
  "mcpServers": {
    "tia-mcp": {
      "command": "${CLAUDE_PLUGIN_ROOT}/server/TiaMcp.Server.exe",
      "args": ["--backend", "openness", "--mode", "ReadWrite"]
    }
  }
}
'@
[IO.File]::WriteAllText("$pluginOut\.mcp.json", $mcpJson, [Text.UTF8Encoding]::new($false))

# 3c. server 载荷(self-contained exe + .NET 10 运行时 + openness-worker\)。
Copy-Item $serverPub "$pluginOut\server" -Recurse -Force

# 3d. 品牌 skills。
Copy-Item $skillsSrc "$pluginOut\skills" -Recurse -Force

$size = [math]::Round((Get-ChildItem $pluginOut -Recurse -File | Measure-Object Length -Sum).Sum / 1MB, 1)
Write-Host "==> 自包含插件就绪: $pluginOut  (~$size MB)"

# 4. 打包成 marketplace(默认):拢成可分发的市场目录 + zip。package-marketplace.ps1 负责打印
#    消费者安装命令与前置。zip 不能直接当 marketplace,故此处不误导用户去 add 裸 zip。
if ($NoMarketplace) {
  Write-Host "    (跳过 marketplace 打包;需要时跑 scripts/package-marketplace.ps1)"
} else {
  & (Join-Path $PSScriptRoot 'package-marketplace.ps1') -Version $Version -PluginDir $pluginOut
}

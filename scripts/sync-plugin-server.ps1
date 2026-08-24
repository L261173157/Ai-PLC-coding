#requires -Version 5.1
<#
.SYNOPSIS
  构建 tia-mcp 并把 server 二进制同步进本机插件安装副本(tia-plugins)。
.DESCRIPTION
  Claude Code 实际加载的 tia-mcp 插件在安装副本(默认
  D:\linxin\Learn\app\SiemensPLC\tia-plugins\plc-siemens),不是仓库 plugins/ 里的源。
  dev-sync-plugins.ps1 只同步 skills;server 的 DLL 靠本脚本走完整链条:

    1) dotnet build TiaMcp.slnx(-SkipBuild 跳过)
    2) -WithWorker:再单独编 net48 worker(它故意不在 slnx 内;
       改 OpennessEngine.cs / worker Program.cs / TiaMcp.Contract 后必用)
    3) 杀运行中的 server(dotnet 宿主跑 TiaMcp.Server.dll)与 worker
       —— 运行中的进程锁着副本 DLL,不杀拷不动("Device or resource busy")
    4) 拷贝构建输出到副本 server\(平铺);-WithWorker 时同步 openness-worker\

  跑完必须在 Claude Code 里 /mcp 重连,新 DLL 才被加载;然后 tia_connect attach。
.EXAMPLE
  ./scripts/sync-plugin-server.ps1                # 常规:build + 同步 server
  ./scripts/sync-plugin-server.ps1 -WithWorker    # 连 worker 一起(改了 worker/Contract)
  ./scripts/sync-plugin-server.ps1 -SkipBuild     # 已构建过,只杀进程 + 同步
#>
param(
  [string]$RepoRoot = (Resolve-Path "$PSScriptRoot\..").Path,
  [string]$PluginServerDir = 'D:\linxin\Learn\app\SiemensPLC\tia-plugins\plc-siemens\server',
  [switch]$SkipBuild,
  [switch]$WithWorker
)
$ErrorActionPreference = 'Stop'

$binNet10 = Join-Path $RepoRoot 'brands\tia\mcp\src\TiaMcp.Server\bin\Debug\net10.0'
$workerProj = Join-Path $RepoRoot 'brands\tia\mcp\src\TiaMcp.Openness.Worker\TiaMcp.Openness.Worker.csproj'
$binNet48 = Join-Path $RepoRoot 'brands\tia\mcp\src\TiaMcp.Openness.Worker\bin\Debug\net48'

if (-not (Test-Path -LiteralPath $PluginServerDir)) {
  throw "插件副本不存在: $PluginServerDir (用 -PluginServerDir 指定实际安装位置)"
}

# --- 1) 构建 ---
if (-not $SkipBuild) {
  Write-Host "构建 TiaMcp.slnx ..."
  dotnet build (Join-Path $RepoRoot 'brands\tia\mcp\TiaMcp.slnx') -v q --nologo
  if ($LASTEXITCODE -ne 0) { throw "dotnet build 失败(退出码 $LASTEXITCODE)" }
}
if ($WithWorker) {
  if (-not $SkipBuild) {
    Write-Host '构建 net48 worker(单独构建,不在 slnx 内)...'
    dotnet build $workerProj -v q --nologo
    if ($LASTEXITCODE -ne 0) { throw "worker 构建失败(退出码 $LASTEXITCODE)" }
  }
  if (-not (Test-Path -LiteralPath $binNet48)) { throw "worker 输出不存在: $binNet48" }
}

# --- 2) 杀锁着 DLL 的进程 ---
# server 以 `dotnet …\TiaMcp.Server.dll` 宿主方式跑(按命令行匹配,别误杀其他 dotnet)。
# worker 不直接强杀:server 一死,worker 的 stdin 收到 EOF,会自行 Dispose TiaPortal
# (优雅释放 headless Portal)再退出。等它最多 10s,超时才强杀兜底——直接强杀持有
# headless Portal 的 worker 会污染 Siemens IPC 栈(FileStorage 等),累积到一定程度
# 新 headless Connect 永久挂死,只能重启机器。
$killed = @()
Get-CimInstance Win32_Process -Filter "Name = 'dotnet.exe'" | Where-Object {
  $_.CommandLine -like '*TiaMcp.Server.dll*'
} | ForEach-Object {
  Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue
  $killed += "$($_.Name) $($_.ProcessId)"
}

$workers = @()
$deadline = (Get-Date).AddSeconds(10)
do {
  $workers = @(Get-Process -Name 'TiaMcp.Openness.Worker' -ErrorAction SilentlyContinue)
  if ($workers.Count -eq 0) { break }
  Start-Sleep -Milliseconds 500
} while ((Get-Date) -lt $deadline)
foreach ($w in $workers) {
  Stop-Process -Id $w.Id -Force -ErrorAction SilentlyContinue
  $killed += "$($w.ProcessName) $($w.Id)(强杀兜底)"
}
if ($workers.Count -eq 0) { Write-Host 'worker 已随 server EOF 优雅退出(无强杀)' }
if ($killed) { Write-Host "已停止: $($killed -join ', ')(释放 DLL 锁)" }
Start-Sleep -Milliseconds 500

# --- 3) 同步 server(平铺覆盖;副本是 publish 形态,只补可替换的托管件,不删除) ---
$serverFiles = Get-ChildItem -File -LiteralPath $binNet10 |
  Where-Object { $_.Extension -notin '.pdb', '.xml' }
foreach ($f in $serverFiles) {
  Copy-Item -LiteralPath $f.FullName -Destination (Join-Path $PluginServerDir $f.Name) -Force
}
Write-Host "OK  server <- $binNet10  ($($serverFiles.Count) 个文件)"

# --- 4) 同步 worker(排除 pdb 与 plc 本地测试目录;exe.config 被 PatchWorkerCodeBase
#     改写过也没关系——spawn 时会按注册表重新打补丁) ---
if ($WithWorker) {
  $workerDst = Join-Path $PluginServerDir 'openness-worker'
  New-Item -ItemType Directory -Force -Path $workerDst | Out-Null
  $workerFiles = Get-ChildItem -File -LiteralPath $binNet48 |
    Where-Object { $_.Extension -notin '.pdb', '.xml' }
  foreach ($f in $workerFiles) {
    Copy-Item -LiteralPath $f.FullName -Destination (Join-Path $workerDst $f.Name) -Force
  }
  Write-Host "OK  worker <- $binNet48  ($($workerFiles.Count) 个文件)"
}

Write-Host ''
Write-Host '完成。现在到 Claude Code 里 /mcp 重连(否则还在跑旧进程),再 tia_connect attach 验证。'

# 构建 TiaMcp Windows 安装包(self-contained server + net48 worker + skills)。
# 在装了 TIA Portal V21 + .NET Framework 4.8 Dev Pack + .NET 10 SDK + Inno Setup(ISCC 在 PATH)的机器上跑。
#
#   ./scripts/build-installer.ps1 -Version 0.2.0
#   ./scripts/build-installer.ps1 -Version 0.2.0 -NoWorker   # worker 已构建
param(
  [Parameter(Mandatory)][string]$Version,     # 如 0.2.0
  [string]$Dist     = "$PSScriptRoot\..\dist",
  [string]$RepoRoot = (Resolve-Path "$PSScriptRoot\..").Path,
  [switch]$NoWorker                            # 跳过 net48 worker 重建
)
$ErrorActionPreference = 'Stop'
# 规整 Version:去掉常见的 'v'/'V' 前缀,并校验为 X.Y.Z(NuGet 版本字符串不接受 'v0.1',
# 且 AssemblyVersion/FileVersion 必须是 4 段纯数字)。Version(InformationalVersion)保持原样。
$Version = "$Version".TrimStart('vV')
if ($Version -notmatch '^\d+\.\d+\.\d+$') {
  throw "Version 必须是 'X.Y.Z' 格式(如 0.2.0),不要带 'v' 前缀。收到: '$Version'"
}
$asm = "$Version.0"   # 0.2.0 -> 0.2.0.0
$mcp       = Join-Path $RepoRoot 'brands\tia\mcp'
$out       = Join-Path $Dist 'TiaMcp'
$iss       = Join-Path $RepoRoot 'installer\tiamcp.iss'
$skillsDir = Join-Path $RepoRoot 'brands\tia\skills'

# 1. 构建 net48 Openness worker(只在 TIA V21 机器上成功)。
if (-not $NoWorker) {
  Write-Host "==> 构建 net48 worker"
  dotnet build "$mcp\src\TiaMcp.Openness.Worker\TiaMcp.Openness.Worker.csproj" -c Release `
    "-p:Version=$Version" "-p:FileVersion=$asm" "-p:AssemblyVersion=$asm"
  if ($LASTEXITCODE -ne 0) { throw "net48 worker 构建失败(dotnet exit $LASTEXITCODE)。" }
}

# 2. 发布 self-contained server。BundleOpennessWorker(AfterTargets=Publish)把第 1 步的
#    worker 拷进 <publish>\openness-worker\。
Write-Host "==> 发布 self-contained server(win-x64)"
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
dotnet publish "$mcp\src\TiaMcp.Server\TiaMcp.Server.csproj" -c Release `
  -r win-x64 --self-contained true -o $out `
  "-p:Version=$Version" "-p:FileVersion=$asm" "-p:AssemblyVersion=$asm"
if ($LASTEXITCODE -ne 0) { throw "self-contained server 发布失败(dotnet exit $LASTEXITCODE)。" }

# 去掉调试符号(可选,减小体积)。
Get-ChildItem $out -Recurse -Filter *.pdb -ErrorAction SilentlyContinue | Remove-Item -Force

# 3. 用 Inno Setup 编译安装包。优先用 PATH 上的 iscc;否则从注册表 InstallLocation 解析
#    ——Inno 静默/默认安装不把自己加进 PATH,且可能装在非系统盘。
Write-Host "==> 编译安装包(ISCC)"
$iscc = (Get-Command iscc -ErrorAction SilentlyContinue).Source
if (-not $iscc -or -not (Test-Path $iscc)) {
  $regKeys = @(
    'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*',
    'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*',
    'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*'
  )
  $loc = foreach ($k in $regKeys) {
    Get-ItemProperty $k -ErrorAction SilentlyContinue |
      Where-Object { $_.DisplayName -like '*Inno Setup*' -and $_.InstallLocation } |
      Select-Object -ExpandProperty InstallLocation -First 1
  }
  if ($loc) {
    $candidate = Join-Path $loc 'ISCC.exe'
    if (Test-Path $candidate) { $iscc = $candidate }
  }
}
if (-not $iscc -or -not (Test-Path $iscc)) {
  throw "找不到 iscc(Inno Setup Compiler):PATH 上没有,注册表也没找到 Inno Setup 安装记录。装 Inno Setup 6(https://jrsoftware.org/isdl.php)或把其目录加进 PATH。"
}
& $iscc /Q "/DVersion=$Version" "/DSourceDir=$out" "/DSkillsDir=$skillsDir" $iss
if ($LASTEXITCODE -ne 0) { throw "ISCC 编译安装包失败(exit $LASTEXITCODE)。" }

$setup = Join-Path $RepoRoot "installer\Output\TiaMcp-Setup-$Version-x64.exe"
if (-not (Test-Path $setup)) { throw "未在预期路径找到安装包: $setup" }
$sha = (Get-FileHash $setup -Algorithm SHA256).Hash
Write-Host "==> OK"
Write-Host "  安装包:  $setup"
Write-Host "  SHA256: $sha"

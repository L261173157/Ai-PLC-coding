; TiaMcp Windows installer (Inno Setup).
;
; Ships the self-contained MCP server (TiaMcp.Server.exe + .NET 10 runtime +
; openness-worker\) plus the skills knowledge layer (markdown). After installing,
; the user copies the generated mcp-client-config.json into their MCP client.
;
; Build (from repo root, on a TIA V21 machine with ISCC on PATH):
;   iscc /Q /DVersion=0.2.0 "/DSourceDir=C:\dist\TiaMcp" "/DSkillsDir=brands\tia\skills" installer\tiamcp.iss
; Output: installer\Output\TiaMcp-Setup-<version>-x64.exe

#ifndef Version
  #define Version "0.2.0"
#endif
#ifndef SourceDir
  ; Self-contained server publish output (TiaMcp.Server.exe + openness-worker\).
  #define SourceDir "..\dist\TiaMcp"
#endif
#ifndef SkillsDir
  #define SkillsDir "..\brands\tia\skills"
#endif

[Setup]
AppName=TiaMcp
AppVersion={#Version}
AppPublisher=TiaMcp
DefaultDirName={localappdata}\Programs\TiaMcp
DefaultGroupName=TiaMcp
DisableProgramGroupPage=yes
; Per-user install: no admin rights needed (self-contained exe, no system registration).
PrivilegesRequired=lowest
Compression=lzma2/ultra64
SolidCompression=yes
ArchitecturesInstallIn64BitMode=x64
ArchitecturesAllowed=x64
OutputDir=Output
OutputBaseFilename=TiaMcp-Setup-{#Version}-x64
UninstallDisplayIcon={app}\TiaMcp.Server.exe
WizardStyle=modern

; English-only by default — keeps the installer compilable even on Inno installs that
; omitted the optional language files (e.g. this machine has no Chinese*.isl). The
; wizard is just a few buttons; the skills content itself is Chinese.
[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
; (1) self-contained server + bundled net48 worker (built by BundleOpennessWorker on publish)
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion
; (2) skills knowledge layer (markdown) — generic MCP usage guidance
Source: "{#SkillsDir}\*"; DestDir: "{app}\skills"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{group}\TiaMcp 使用手册"; Filename: "{app}\skills\overview\SKILL.md"
Name: "{group}\卸载 TiaMcp"; Filename: "{uninstallexe}"

[Run]
; Pop up the ready-to-copy config so the user sees what to paste into their MCP client.
Filename: "notepad.exe"; Parameters: """{app}\mcp-client-config.json"""; Flags: nowait postinstall skipifsilent unchecked; Description: "查看 MCP 客户端配置(复制进 Claude Code / Cursor 等)"

[Code]
// --- Prerequisite checks at startup (TIA V21 + Siemens Openness group) ---
// The server exe installs fine regardless, but actually connecting to TIA needs
// both. Warn interactively (with concrete fixes); silent installs skip the prompt.
function IsTiaV21Installed: Boolean;
begin
  Result := RegKeyExists(HKLM, 'SOFTWARE\Siemens\Automation\Openness\21.0');
end;

function IsInOpennessGroup: Boolean;
var
  ExitCode: Integer;
begin
  // PowerShell exits 0 if the current user is in the group, 1 otherwise.
  if Exec('powershell.exe',
    '-NoProfile -Command "exit [int](-not ((whoami /groups) -match ''Siemens TIA Openness''))"',
    '', SW_HIDE, ewWaitUntilTerminated, ExitCode) then
    Result := (ExitCode = 0)
  else
    Result := False;
end;

function InitializeSetup(): Boolean;
var
  Msg: string;
begin
  if IsTiaV21Installed and IsInOpennessGroup then
  begin
    Result := True;
    exit;
  end;

  Msg := '前置条件不满足 —— TiaMcp 会装上,但连接 TIA 前需先处理:' + #13#10 + #13#10;
  if not IsTiaV21Installed then
    Msg := Msg +
      '• TIA Portal V21(含 Openness):未检测到。' + #13#10 +
      '   → 安装 TIA Portal V21 时勾选 Openness 组件。' + #13#10;
  if not IsInOpennessGroup then
    Msg := Msg +
      '• Siemens TIA Openness 本地组:当前用户不在组内(connect 会抛 COMException)。' + #13#10 +
      '   → 管理员 PowerShell 运行:' + #13#10 +
      '     Add-LocalGroupMember -Group "Siemens TIA Openness" -Member "' + GetUserNameString + '"' + #13#10 +
      '   → 然后注销并重新登录(刷新令牌)。' + #13#10;
  Msg := Msg + #13#10 + '仍要现在安装吗?';

  Result := (SuppressibleMsgBox(Msg, mbConfirmation, MB_YESNO, IDYES) = IDYES);
end;

// Generate mcp-client-config.json pointing at the installed exe. Triggered at
// ssPostInstall (runs before the [Run] section), so the notepad popup above sees it.
// JSON is built inline (no template-file I/O) to sidestep Inno's AnsiString file APIs;
// install-dir backslashes -> forward slashes so the path is valid JSON (Windows and
// MCP clients both accept forward slashes).
procedure GenerateClientConfig;
var
  AppDir, JsonStr: string;
  AnsiJson: AnsiString;
begin
  AppDir := ExpandConstant('{app}');
  StringChange(AppDir, '\', '/');
  JsonStr := '{' + #13#10 +
    '  "mcpServers": {' + #13#10 +
    '    "tia": {' + #13#10 +
    '      "command": "' + AppDir + '/TiaMcp.Server.exe",' + #13#10 +
    '      "args": ["--backend", "openness", "--mode", "ReadWrite"]' + #13#10 +
    '    }' + #13#10 +
    '  }' + #13#10 +
    '}';
  AnsiJson := JsonStr;
  SaveStringToFile(ExpandConstant('{app}\mcp-client-config.json'), AnsiJson, False);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    GenerateClientConfig;
end;

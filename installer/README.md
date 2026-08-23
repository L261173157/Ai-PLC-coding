# TiaMcp Windows 安装器

把 TiaMcp(self-contained MCP server + worker + skills 知识层)打成一个**双击即装**的 `TiaMcp-Setup-<version>-x64.exe`,分发给**不装 .NET SDK、不走 GitHub** 的终端用户。

## 工作原理

- **self-contained server**:`dotnet publish --self-contained true -r win-x64` 产出(含 .NET 10 运行时);publish 时 `BundleOpennessWorker` target 自动把 net48 worker 拷进 `openness-worker\`。
- **安装器**:Inno Setup,免管理员(`PrivilegesRequired=lowest`,装到 `%LocalAppData%\Programs\TiaMcp`)。
- **配置片段**:安装结束由 `[Code]` 从模板生成 `mcp-client-config.json`(指向已装 exe),用户复制进 MCP 客户端即可。

## 发布机前置(一次性)

在**装了 TIA Portal V21** 的机器上:

1. **.NET Framework 4.8 Developer Pack**(构建 net48 worker)。
2. **.NET 10 SDK**(`dotnet build/publish`)。
3. **Inno Setup 6**(编译 `.iss`)—— 安装后把 `ISCC.exe` 所在目录(默认 `C:\Program Files (x86)\Inno Setup 6`)加进 `PATH`。
4. 当前 Windows 用户在 `Siemens TIA Openness` 本地组(否则 worker 握手失败)。

## 出包

```powershell
# 仓库根目录
./scripts/build-installer.ps1 -Version 0.2.0
```

产物:`installer\Output\TiaMcp-Setup-0.2.0-x64.exe`(控制台会打印 SHA256)。

## 消费者侧

1. 双击 `TiaMcp-Setup-0.2.0-x64.exe` → 装到 `%LocalAppData%\Programs\TiaMcp`(无需管理员)。
2. 安装结束会弹出 `mcp-client-config.json`(形如下),复制进 MCP 客户端的配置:
   ```json
   {"mcpServers":{"tia":{"command":"C:/Users/<user>/AppData/Local/Programs/TiaMcp/TiaMcp.Server.exe","args":["--backend","openness","--mode","ReadWrite"]}}}
   ```
3. 开始菜单 →「TiaMcp 使用手册」打开 skills 总览。

消费者整机只需:**TIA Portal V21 + 在 `Siemens TIA Openness` 组**。无需 .NET SDK / 运行时(self-contained)。

## 文件

| 文件 | 作用 |
|---|---|
| `tiamcp.iss` | Inno Setup 脚本(打包 + `[Code]` 内联生成配置片段 + 开始菜单快捷方式) |
| `../scripts/build-installer.ps1` | 串联 build worker → publish server → ISCC 编译 |

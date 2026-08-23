# plc-siemens 插件(tia-mcp)

西门子 TIA Portal 的品牌插件:把 `brands/tia/mcp/` 的 TiaMcp server(`tia-mcp` MCP server)+ `brands/tia/skills/` 的 TIA 工程技能打包成一个 Claude Code 插件。

插件 `name` 仍为 `tia-mcp`(保住 `/tia-mcp:*` slash 命令连续性)。

## 开发(dev,就地)
- **MCP server**:`.mcp.json` 用 `${CLAUDE_PLUGIN_ROOT}/../../brands/tia/mcp/...` 指向源,`dotnet run` 启动(需本机 .NET SDK)。
- **skills**:源在 `../../brands/tia/skills/`,本目录下 `skills/` 由 [`scripts/dev-sync-plugins.ps1`](../../scripts/dev-sync-plugins.ps1) 同步(派生,gitignored)。改完 skills 跑一次该脚本即可。

加载:`claude --plugin-dir plugins/plc-siemens`,会话内 `/mcp` 应见 `tia-mcp`。

## 发布(自包含插件,marketplace 用)
[`scripts/build-plugin.ps1`](../../scripts/build-plugin.ps1)(无参即取源 `plugin.json` 的版本)产出 `dist/plugins/plc-siemens/`:
`.claude-plugin/plugin.json` + publish 模式 `.mcp.json`(直指打包进来的 `server/TiaMcp.Server.exe`)+ `server/`(win-x64 self-contained publish + `openness-worker/`)+ 复制好的 `skills/`。

zip 后走 marketplace——`claude` CLI(终端跑,**非** `/plugin` 斜杠命令):`claude plugin marketplace add <目录>` + `claude plugin install tia-mcp@tia-plugins`;**升级**(新 build 覆盖市场目录后)`claude plugin marketplace update tia-plugins` + `claude plugin update tia-mcp@tia-plugins`;二者均**重启 Claude Code** 生效。消费者**无需 .NET SDK**(self-contained)。需在 TIA V21 机器上跑(同 `build-installer.ps1` 前置)。

> 终端用户**双击安装包**分发则用根 `installer/` + [`scripts/build-installer.ps1`](../../scripts/build-installer.ps1)。

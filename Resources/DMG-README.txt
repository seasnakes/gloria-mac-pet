GLORIA · Mac 桌宠

安装
  把 GLORIA 拖进旁边的「Applications（应用程序）」文件夹，然后从「应用程序」里打开。
  她不占 Dock，会出现在菜单栏，右键角色本人也能打开菜单。

第一次打开时 macOS 提示「无法验证开发者」或「已损坏」
  这个版本没有经过 Apple 公证。放行一次就好，任选一种：
  1. 先双击打开一次（会被拦下），再打开「系统设置 › 隐私与安全性」，在下方点「仍要打开」；
  2. 或者在终端运行：xattr -dr com.apple.quarantine /Applications/GLORIA.app
  （macOS 14 还可以在「应用程序」里右键 GLORIA →「打开」；macOS 15 起这个办法不再可用。）

需要
  macOS 14.2 或更新；Apple Silicon 和 Intel Mac 都可以。

让她当 AI 编程助手的化身（Claude Code / Codex / Cursor / DSH）
  第一次打开时，她会看看这台 Mac 上装了哪些（~/.claude、~/.codex、~/.cursor、~/.dsh），弹窗问你要接哪几个。
  勾上的会在对应的钩子文件里加入 GLORIA 的条目（改之前会备份），重开一次那个 agent 就生效：
    Claude Code  ~/.claude/settings.json
    Codex        ~/.codex/hooks.json      第一次会问要不要信任这些钩子，选信任
    Cursor       ~/.cursor/hooks.json     编辑器和 cursor-agent 都算，新开对话就生效
    DSH          ~/.dsh/hooks.json        DSH 还要装官方 hooks 桥接插件 @deepseek-ai/dsh-hooks-claude-code
                                          （连同 dsh-hook-protocol），configPath 指向这个文件；命令见项目主页
  以后随时在菜单 ›「<名字> 化身」› 接入 里开关。挪动过 App 的位置后重新打开一次，钩子会自动指向新位置。

卸载
  退出 GLORIA（菜单 › 退出），先在菜单里关掉各个「化身」的接入，再把 GLORIA 拖进废纸篓。

项目主页：https://github.com/seasnakes/gloria-mac-pet

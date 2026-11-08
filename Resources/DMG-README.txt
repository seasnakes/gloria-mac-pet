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

让她当 Claude Code / Codex 的化身
  菜单 › Claude Code 化身 / Codex 化身 › 接入。会在 ~/.claude/settings.json 或 ~/.codex/hooks.json 里加入
  GLORIA 自己的钩子（改之前会备份）。挪动过 App 的位置后重新打开一次，钩子会自动指向新位置。

卸载
  退出 GLORIA（菜单 › 退出），先在菜单里关掉两个「化身」的接入，再把 GLORIA 拖进废纸篓。

项目主页：https://github.com/seasnakes/gloria-mac-pet

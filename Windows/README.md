# GLORIA · Windows 桌宠

完整解压 ZIP 到固定文件夹，双击 **GLORIA.exe**。不需要管理员权限、Python 或另装开发工具。目标环境为 Windows 10 / 11（桌面版，.NET Framework 4.8 或更新版本）。请将两个 EXE 和 characters 文件夹放在一起。

第一次会在主显示器右下角看到 GLORIA。拖动她可以换位置；单击打招呼，双击跳舞。右键角色或任务栏通知区域的 GLORIA 图标可打开菜单（图标可能在「显示隐藏的图标」里）。双击托盘图标或再次运行 GLORIA.exe 可召回角色。

- **出场角色**：GLORIA、Doro、抽象乌萨奇、GrokBot 和 14 酱可同时显示。GrokBot 和 14 酱使用 WPF 实时渲染 3D 模型。让它出场后，把鼠标沿着脸旁绕一圈，它会跟着转头、抬头和低头；移远后回到待机。收到 Agent 事件时优先演对应动作。
- **坐在前台窗口上**：有足够空间时坐到活动窗口上沿；关闭后可自己摆放角色。
- **随电脑播放的声音跳舞**：读取默认播放设备的音量，任何有声播放都可能触发；不使用麦克风。电脑空闲三分钟会打盹。
- **Agent 化身**：在相应子菜单里选角色、点「接入此 Agent」，再重开 Agent 会话。Claude Code 默认对应乌萨奇，Codex 对应 Doro；事件到达时对应角色会出场。要让 GLORIA 为 Claude 报信，在 Claude Code 的子菜单中选她即可。
- **跨 Agent 传信**：例如 Claude Code 直接运行 `codex exec "…"`，乌萨奇会走近 Doro，抛出粉色爱心信封。Doro 会在委托期间保持忙碌，工具完成或报错后把信送回。也识别直接调用其他 Agent 的 MCP 工具和指定该 Agent 的子代理。两个 Agent 使用同一角色时用气泡报信。右键选「演示：Claude 请 Codex 帮忙」即可试玩。
- **贴边探头**：把角色慢慢拖到显示器左侧或右侧松手，也可在右键菜单选「躲到屏幕边上偷看」。鼠标快速靠近会缩回，停在旁边会再探出头；工作、说话或等批准时会探出来。双击或拖走可回到桌面。与另一台显示器相接的边缘不会触发。右键菜单里可关闭拖放触发。
- Claude Code 使用支持 command + args 的版本。Codex 需要在客户端信任新钩子；从旧版升级且原已接入 Cursor 时，请在「Agent 化身 → Cursor」点一次「刷新接入」以接收委托开始事件；DSH 还需单独配置官方 hooks 桥接插件。只支持同一 Windows 用户桌面内的原生客户端，不直接接收 WSL 内的事件。
- 气泡会显示开工、读写文件、等待批准、完成等提示，以客户端实际发送的事件为准。桌宠不会批准操作，不记录原始命令、输入内容或回复。接入/断开仅增删本程序的钩子，原配置存在时会在旁边生成 .gloria-*.bak。
- Windows 版未实现 Mac 版的窗口内点击定位和完整的甩落物理。

位置、大小、角色和偏好保存在 %LOCALAPPDATA%\GLORIA\windows.json。移动或删除程序之前，请先在各 Agent 子菜单断开已接入的钩子。要卸载，退出托盘程序并删除解压目录即可。

**从源码构建**：在仓库根目录运行 `powershell -NoProfile -ExecutionPolicy Bypass -File Windows\build.ps1`。压缩包在 dist 下。Windows 构建直接使用随仓库提供的三角网格与动作数据。修改 GrokBot 原模型或 Mac 版姿势后，在 Mac 的仓库根目录运行 `bash Windows/export-grokbot/export.sh` 即可重新导出。更新 14 酱时运行 `bash Windows/export-grokbot/export.sh fourteen`，从 Mac 版 USDZ 重新导出头发网格、贴图和动作。

代码使用随包的 MIT LICENSE。GLORIA 原创美术保留所有权利；Doro 来自 DoroPet 的模型渲染，抽象乌萨奇是《ちいかわ》（©ナガノ）的同人二创，仅供个人非商业使用。GrokBot 是作者 Blender 工程导出的 3D 模型，机身标志属于 xAI。角色美术不在代码 MIT 许可范围内。

# GLORIA · Windows 桌宠

完整解压 ZIP 到固定文件夹，双击 **GLORIA.exe**。不需要管理员权限、Python 或另装开发工具。目标环境为 Windows 10 / 11（桌面版，.NET Framework 4.8 或更新版本）。请将两个 EXE 和 `characters` 文件夹放在一起。

第一次启动会落到当前显示器窗口顶边或任务栏上沿，挥手打招呼。拎起后颈拖动会像钟摆一样晃动，松手可甩出去弹跳落座；单击摸摸头冒泡（连戳 5 下会生气），双击开心跳起。透明区域完全点击穿透，只有点在角色本体上才会拦截鼠标。右键角色或点击任务栏通知区域的图标可打开完整菜单，双击托盘图标或再次运行 `GLORIA.exe` 可召回角色。

- **出场角色与独立台词**：GLORIA、Doro、抽象乌萨奇、GrokBot 和 14 酱可同时出场，也支持读取 `%LOCALAPPDATA%\GLORIA\characters\<id>` 下导入的自定义角色。所有角色在待机时都会跟随附近光标转头/看过来；多只同处一处时会自动错开并互相招手打招呼。GrokBot 和 14 酱使用硬件加速 3D 实时渲染，静止时自动休眠零额外开销。
- **窗口站立、遮挡跳起与真实甩落物理**：使用 DWM 真实可见边框识别所有桌面窗口上沿，拖到任意窗口顶边松手即可垂腿坐下，拖动窗口时跟着走，窗口关闭时落下，被前方窗口遮挡时会自动跳到上层窗口。支持跨显示器抛投和一键「移到显示器」；可选开启「切换窗口时自动跳过来」。
- **日常小动作**：支持自己溜达、追逐滑过脚边的光标扑一下（停在光标旁不挡点击）、电脑空闲三分钟自动打盹冒 `z` 泡，动一下鼠标键盘即醒。
- **随音乐与节拍跳舞**：通过 WASAPI 按进程识别音乐软件（网易云音乐、QQ 音乐、Spotify、Apple Music、PotPlayer 等）与浏览器播放，忽略系统提示音，并使用内存级 Loopback 实时节拍跟踪（自动测算 BPM，浏览器语音不误触发）演绎 16 拍完整舞步与音符粒子，无需麦克风或录音权限。
- **四个 Agent 的化身与子代理分身**：支持 Claude Code、Codex、Cursor 与 DSH。展示柔彩三态独立气泡（对话、轻字幕、常驻审批提醒 + 提示音），实时播报正在读改的文件与运行的命令；派出子代理时身边会跑出迷你分身，压缩上下文时触发闪烁特效，完成时播报回复摘要。
- **跨 Agent 传信与 Computer Use 演出**：例如 Claude Code 调用 `codex exec "…"` 或对方 MCP 工具，调用方化身会走过去抛出粉色爱心信封，对方忙碌处理完后再把信封抛回。Agent 执行 `cua_repl` 或 `computer` 操作电脑时，化身会跳到目标窗口上点击、敲键盘、举相机截图、拖拽和滚动（自动按目标方向镜像转身），并在屏幕上画出点击波纹、取景框闪光与拖拽虚线。
- **贴边探头**：慢慢拖到外侧屏幕左右边缘松手，或在菜单选「躲到屏幕边上偷看」。鼠标快速冲来会缩回去，停在旁边会被发现，说话或等批准时整个探出来；双击跳回桌面。
- **命令行控制（`--ctl`）**：与 Mac 版一致，支持 `GLORIA.exe --ctl "{\"ctl\":\"status\"}"`、`dance`、`cua`、`together`、`demo`、`show`、`role`、`throw`、`peek`、`desk`、`menu` 等控制命令。

位置、大小、角色和偏好保存在 `%LOCALAPPDATA%\GLORIA\windows.json`。移动程序目录后重新启动会自动更新已接入钩子的路径；删除程序前可在各 Agent 子菜单断开钩子。

**从源码构建与自检**：在仓库根目录运行 `powershell -NoProfile -ExecutionPolicy Bypass -File Windows\build.ps1`，产物在 `dist` 下；运行 `powershell -NoProfile -ExecutionPolicy Bypass -File Windows\smoke.ps1 -Zip dist\GLORIA-0.9.0-Windows.zip -Desktop` 可执行完整配置与桌面交互自检。

代码使用随包的 MIT LICENSE。GLORIA 原创美术保留所有权利；Doro 来自 DoroPet 的模型渲染，抽象乌萨奇是《ちいかわ》（©ナガノ）的同人二创，仅供个人非商业使用。GrokBot 是作者 Blender 工程导出的 3D 模型，机身标志属于 xAI。角色美术不在代码 MIT 许可范围内。

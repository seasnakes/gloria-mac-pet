<div align="center">

<img src="docs/icon.png" width="128" alt="GLORIA 图标">

# GLORIA · Mac 桌宠

**坐在窗口边陪你写代码的 Mac 小桌宠。<br>平时跟着音乐跳舞，干活时替 Claude Code、Codex、Cursor 和 DSH 报信、传话、演 computer use。**

<a href="#安装"><img src="https://img.shields.io/badge/macOS-14.2%2B-ff9ec8?style=flat-square&logo=apple&logoColor=white" alt="macOS 14.2+"></a>
<img src="https://img.shields.io/badge/Apple%20Silicon%20%2B%20Intel-universal-b9a7f0?style=flat-square" alt="Apple Silicon + Intel">
<img src="https://img.shields.io/badge/Swift-AppKit%20%2B%20SceneKit-F05138?style=flat-square&logo=swift&logoColor=white" alt="Swift">
<a href="https://github.com/seasnakes/gloria-mac-pet/releases/latest"><img src="https://img.shields.io/badge/下载-DMG-7fd3f5?style=flat-square" alt="下载 DMG"></a>
<img src="https://img.shields.io/badge/version-0.8.0-7fd3f5?style=flat-square" alt="v0.8.0">
<img src="https://img.shields.io/badge/联网-不需要-8fd19e?style=flat-square" alt="不联网">
<a href="LICENSE"><img src="https://img.shields.io/badge/代码-MIT-f7c873?style=flat-square" alt="代码 MIT"></a><br>
<a href="#四个-agent-的化身"><img src="https://img.shields.io/badge/Claude%20Code-化身-D97757?style=flat-square&logo=claude&logoColor=white" alt="Claude Code 化身"></a>
<a href="#四个-agent-的化身"><img src="https://img.shields.io/badge/Codex-化身-412991?style=flat-square&logo=openai&logoColor=white" alt="Codex 化身"></a>
<a href="#四个-agent-的化身"><img src="https://img.shields.io/badge/Cursor-化身-2563EB?style=flat-square" alt="Cursor 化身"></a>
<a href="#dsh"><img src="https://img.shields.io/badge/DSH-化身-5C6BC0?style=flat-square" alt="DSH 化身"></a>
<a href="#computer-use"><img src="https://img.shields.io/badge/computer%20use-会演-ff9ec8?style=flat-square" alt="computer use"></a><br>
<a href="https://github.com/seasnakes/gloria-mac-pet/stargazers"><img src="https://img.shields.io/github/stars/seasnakes/gloria-mac-pet?style=flat-square&logo=github&color=ff9ec8&cacheSeconds=3600" alt="stars"></a>
<a href="https://github.com/seasnakes/gloria-mac-pet/commits/main"><img src="https://img.shields.io/github/last-commit/seasnakes/gloria-mac-pet?style=flat-square&color=b9a7f0&cacheSeconds=3600" alt="last commit"></a>
<img src="https://img.shields.io/github/repo-size/seasnakes/gloria-mac-pet?style=flat-square&color=7fd3f5&cacheSeconds=3600" alt="repo size">

<img src="https://count.getloli.com/@seasnakes-gloria-mac-pet?name=seasnakes-gloria-mac-pet&theme=cat-girls-yay&padding=7&offset=0&align=top&scale=1&pixelated=1&darkmode=auto" alt="浏览量">

<img src="docs/hero.gif" width="720" alt="Doro、GLORIA、乌萨奇站在窗口上跟着音乐跳舞">

</div>

---

做这个小工具，最开始是觉得一个人对着屏幕 vibe coding 有点冷清，想在窗口边放几个会动的小家伙添点气氛。

写着写着顺手解决了几个平时用 agent 的小痛点：

- 平时不知道 agent 在后台具体跑什么命令，接上钩子后，她会在头顶用一行小字幕报给你看。
- 同时开好几个 agent 协作时，经常分不清分工走到哪一步了。现在每个 agent 有自己的化身，谁在忙、谁卡着等你批准、谁把活交给了谁，扫一眼桌面就知道。
- 给 computer use 添点趣味。agent 操作电脑时，它的化身会直接跑到那个窗口上「动手」点按、打字和截图。

## 内置四个角色，可以同时出现

<img src="docs/cast.png" width="820" alt="三个手绘角色的主要动作：站着、坐窗沿、思考、等批准、跳舞、打盹、被拎起来、看光标">

菜单「角色」里勾选谁出场。几个角色同时在桌面上时会互相打招呼，不会叠在同一个位置。这四个桌宠背后刚好是四种不同的技术实现：

- GLORIA（柔彩）是帧动画。走的是 Codex 桌宠常见的路子，用 GPT image（`image_gen`）逐帧生成关键帧，跳舞、思考和操作电脑的过渡帧用光流推出来，不叠影，每个像素都来自原画。
- Doro 是 Live2D 加矢量绘制，取自本机 DoroPet-macos（基于 [DoroPet](https://github.com/waterfeet/DoroPet_V3)）的站立版。头是 Live2D，身体和键盘、相机这些道具用 `QPainter` 程序绘制，再离屏逐帧渲染。待机时带呼吸、轻微摇头和眨眼。
- 抽象乌萨奇只用了一张毛绒玩偶的照片做骨骼绑定。拆分身体部件、建好 ARAP 网格后，用 Animated Drawings 的重定向算法把常见动作格式（BVH）和关节角映射上去。
- GrokBot 是用 Codex 在 Blender 里建模的 3D 模型（变形短片的完成造型），导出成 USDZ，App 用 Apple 的 3D SDK SceneKit 每帧实时渲染。

### GrokBot 的实时 3D

<img src="docs/grokbot-3d.gif" width="340" alt="GrokBot 转身看着绕圈的光标，然后点击、在全息键盘上打字、截图、跳舞">

GrokBot 不是预先渲好的序列帧，而是每帧实时画进它自己的透明窗口里。鼠标晃过时，它会真的按光标的确切方向转身去看，不是在 16 张固定朝向的图之间切换。

耳朵天线、鳍手、九种 LED 表情、悬浮环和短片里的 3D 光标都是模型本身的独立部件，由 App 实时摆动，动作之间平滑插值，不会跳帧。坐窗沿、被拎起来甩、跳舞、当 agent 化身、操作电脑，用的都是和其他角色同一套状态。鼠标点没点中它，直接用射线检测 3D 几何体。模型由 `characters/grokbot/export_grokbot.py` 从 Blender 工程导出，取完成造型那一帧，保留各部件轴心并烘焙修改器。

## 四个 agent 的化身

<img src="docs/together.gif" width="585" alt="乌萨奇（Claude Code）把信封扔给 Doro（Codex），Doro 干完活再扔回来">

现在能当化身的 agent 有四个，可以在菜单里给它们各挑一个角色，比如让乌萨奇当 Claude、Doro 当 Codex：

- Claude Code，钩子写在 `~/.claude/settings.json`。
- Codex，钩子写在 `~/.codex/hooks.json`。Codex 第一次启动时会问要不要信任这些钩子，选信任就行。
- Cursor，钩子写在 `~/.cursor/hooks.json`，编辑器里的 Agent 和命令行的 `cursor-agent` 都算，新开一个对话就生效。`cursor-agent -p` 不发「开始/结束回合」事件，所以她以会话结束当作做完。
- DSH（DeepSeek Harness），钩子写在 `~/.dsh/hooks.json`，配置方式见下一小节。

接到任务时化身会跳一下，干活时摆思考动作，头顶小字幕实时报出正在读、改、跑什么。需要你批准时她会举牌提醒并一直挂着气泡；做完了会说出回复的第一句。如果派出了子代理，身边会跑出一个小分身，子代理结束时挥手消失。

任意两个 agent 之间还能互相委托。比如 Claude 在命令行里跑 `codex exec "…"`、`cursor-agent -p "…"` 或 `dsh …`，或者反过来跑 `claude -p`，调用方的化身会走过去，把一个小信封扔给被调用方的化身。对方干活期间一直处在忙碌状态，干完再把信封扔回来。调用对方的 MCP 也算委托。

她们只看不动手，不会替你批准任何操作。

### DSH

DSH 的钩子写在 `~/.dsh/hooks.json`，格式和 Claude Code 一样。DSH 自己不读这个文件，要给它装官方的 hooks 桥接插件。DSH 的每个 profile（`web`、`headless`……）各装一次，版本号要和 `dsh --version` 一致：

```bash
npm i -g @deepseek-ai/dsh            # 需要 Node 24.9 以上
v=$(dsh --version)
dsh plugin --profile web add @deepseek-ai/dsh-hooks-claude-code@$v @deepseek-ai/dsh-hook-protocol@$v
```

然后在这个 profile 的 `~/.dsh/profiles/web/cordis.patch.yml` 里挂上它，`configPath` 写绝对路径：

```yaml
- insert:
    - id: hooks-claude-code
      name: '@deepseek-ai/dsh-hooks-claude-code'
      config:
        configPath: /Users/你/.dsh/hooks.json
```

`dsh-hook-protocol` 是桥接插件的依赖，DSH 不自带，漏装的话启动时会提示 `hooks-claude-code … failed to import`。

DSH 的工具名（`read` / `edit` / `pwsh` / `web_search` 等）会被翻成和其他 agent 一样的小字幕。这条路在 DSH 0.2.2-rc.1 上接通过。

### 新电脑上要配置什么

新电脑上第一次打开 GLORIA，她会检查这台 Mac 上有没有 `~/.claude`、`~/.codex`、`~/.cursor` 和 `~/.dsh`，弹一个窗口列出找到的 agent。勾上的就会自动装好钩子（改之前会先备份原文件），之后重开一次那个 agent 就生效，Cursor 新开一个对话就行。

当时没装或者不想接的，以后随时在菜单「`<agent> 化身` › `接入`」里开关。除此之外，Claude Code、Codex 和 Cursor 里不需要额外配置；只有 DSH 需要装上面提到的插件。

## Computer use

<img src="docs/computer-use.gif" width="760" alt="Codex 操作 Chrome：Doro 举相机截图、敲键盘打字、拖着光标划过去，点到的地方泛起波纹">

Agent 操作电脑时，它的化身会跑到那个窗口上跟着比划。

窗口顶上能站就跳上去；要是窗口铺满了屏幕，她就沿着屏幕底部走到对应位置的旁边。点击时伸手点，打字时敲小键盘，截图时举相机，拖拽时拽着光标走，滚动时往下划。目标在另一侧时，姿势会自动镜像过去。

点到的地方会泛起波纹，双击是两圈；截图时窗口闪一下，四角出现取景框；拖拽时画出一条虚线。头顶小字幕会显示「Google Chrome：点一下」「按 ⌘⇧T」这类说明。打字只显示字数，不显示具体内容，密码不会出现在气泡里。

<img src="docs/computer-use.png" width="620" alt="三个手绘角色的操作电脑姿势：点击、打字、截图、拖拽、滚动（GrokBot 的见上面的 3D 动图）">

目前能识别的调用包括：

- Codex 在 `cua_repl` 里调用的 `getApp`、`click`、`typeText`、`pressKey`、`scroll`、`drag`、`getScreenshot` 和 `getAXState`。同一个会话后面的调用会沿用前面选中的 App。
- Claude Code 的 computer use 工具（`left_click`、`type`、`key`、`screenshot`、`scroll`、`left_click_drag` 等），以及浏览器的 `computer` 工具。

## 平时在桌面上还会干什么

平时不忙的时候，她们就在桌面上自己待着：

- 坐在窗口顶边，窗口移动就跟着走，窗口关掉就掉下去，被别的窗口挡住会自己跳上来。落到 Dock 和屏幕底部时改成站姿。
- 揪着后颈拎起来会手脚乱扑腾；松手后按甩出去的速度飞走，撞墙会反弹，摔重了还会晕一会儿。有多块显示器时可以直接拖到另一块屏幕上，扔得够高也能飞过去；菜单里的「移到显示器」可以一键换屏。
- 拖到屏幕左右边缘松手，她会贴边躲到屏幕外面，只歪着探出半个脑袋，眼睛跟着光标转。光标冲过来她就缩回去，只留一撮头发；光标停在旁边久了，她就知道被发现了，探出来多一点。有话要说或者 agent 等批准时会整个探出来。双击把她叫出来，也可以直接拖走。
- 音乐 App 一出声就开始跳，一拍一个舞步。给了“系统录音”权限就能跟上真实节拍，声音只在内存里分析。
- 光标在她站的地方晃，她会跑到光标旁边扑一下，不会钻到光标底下挡住点击。连戳 5 下会生气。
- 站在 Dock 上时，放大的图标会把她顶起来；有 App 启动、图标弹跳时，她也跟着蹦。
- 3 分钟没碰键盘鼠标就会打盹，一动就醒。

鼠标只有点在她们身上不透明的地方才会被拦住，别处照常点到下面的窗口。

## 安装

去 [Releases](https://github.com/seasnakes/gloria-mac-pet/releases/latest) 下载 `GLORIA-<版本>.dmg`，打开后把 GLORIA 拖进「应用程序」。需要 macOS 14.2 或更新版本，Apple Silicon 和 Intel 都能用。

这个版本没有经过 Apple 公证，第一次打开时 macOS 会拦下来，放行一次就好：

1. 先双击打开一次，再到「系统设置 › 隐私与安全性」，在下方点「仍要打开」；
2. 或者在终端运行 `xattr -dr com.apple.quarantine /Applications/GLORIA.app`。

macOS 14 也可以在「应用程序」里右键 GLORIA 选「打开」。

从源码构建只需要 Xcode 或 Command Line Tools，没有别的依赖：

```bash
make install    # = bash build.sh --install：构建、装进 /Applications 并启动
make dmg        # = bash build.sh --dmg：做出 dist/GLORIA-<版本>.dmg（arm64 + x86_64）
```

如果有 Apple Developer ID，设上 `SIGN_ID="Developer ID Application: …"` 就会用它给 DMG 里的 App 签名；再设 `NOTARY_PROFILE` 还会公证 DMG，别人打开时不会被拦。推送 `v*` 标签时，GitHub Actions（`.github/workflows/release.yml`）会自动构建 DMG 并挂到对应的 Release 上。

她们不占 Dock。菜单栏里有一排出场角色的小头像，右键角色本人也能打开同一个菜单。找不到她们时点「叫她们过来」，开机自启在菜单里勾选「登录时启动」。

<details>
<summary><b>菜单一览</b></summary>

- 角色：勾选谁出场，可以多选（导入的 dsh-pet 宠物也在这里）
- Claude Code 化身 / Codex 化身 / Cursor 化身 / DeepSeek Harness 化身：接入（装钩子）、由谁来当
- 需要批准时提示音 · 叫她们过来 · 移到显示器（有多块屏幕时） · 躲到屏幕边上偷看 · 大小 · 自己溜达 · 闲着会打盹 · 追光标 · 拖到屏幕左右边缘会躲起来探头 · 登录时启动
- 随音乐跳舞 · 跟着节拍 · 浏览器里的音乐也跳
- 演示：跳舞 16 秒 / Claude Code 一轮工作 / Claude 请 Codex 帮忙 / Codex 操作电脑 / 被扔出去 / 被拎起来晃一晃 / 打个盹 10 秒

右键某个角色时，菜单只显示她自己，并多一项「让她先休息（隐藏）」。

</details>

<details>
<summary><b>化身钩子会改哪些文件</b></summary>

接入时会改写下面四个文件里的对应配置：

- Claude Code：`~/.claude/settings.json`
- Codex：`~/.codex/hooks.json`
- Cursor：`~/.cursor/hooks.json`
- DSH：`~/.dsh/hooks.json`

每次只增删 GLORIA 自己的条目，其他设置和钩子都保留。改之前会把原文件备份到 `~/Library/Application Support/GLORIA/hook-backups/`；改完后文件会重新排版，键按字母排序。

钩子命令是 `GLORIA_HUSK=1 '<GLORIA.app>/Contents/MacOS/GLORIA' --hook claude|codex|cursor|dsh`。它通过私有 Unix 套接字转发精简后的事件，耗时约 10 ms，不输出任何内容；GLORIA 没开时直接退出。computer use 的调用只转发动作和坐标，打的字只转发字数。挪动了 App 之后重新打开一次，钩子会自动指向新位置。

</details>

<details>
<summary><b>命令行与自检</b></summary>

```bash
X=/Applications/GLORIA.app/Contents/MacOS/GLORIA
$X --hooks claude|codex|cursor|dsh on|off                 # 同菜单开关
$X --render-sheet <角色 id> <输出.png> [倍数]             # 导出整张精灵图（3D 的 GrokBot 按姿势逐格渲染，放大不糊）
$X --ctl '{"ctl":"status"}'                               # 所有角色的状态（JSON）
$X --ctl '{"ctl":"show","id":"grokbot","on":true}'        # 放出 / 收起角色：gloria / doro / usagi / grokbot / 导入的名字
$X --ctl '{"ctl":"role","source":"codex","id":"doro"}'    # 指定化身
$X --ctl '{"ctl":"cua"}'                                  # 演示 Codex 操作电脑
$X --ctl '{"ctl":"together"}'                             # 演示 Claude 请 Codex 帮忙
$X --ctl '{"ctl":"dance","seconds":12}'
$X --ctl '{"ctl":"play","anim":"type","pet":"usagi"}'     # 让某个角色做某个动作（tap / type / snap / drag / scroll …）
$X --ctl '{"ctl":"throw","vx":-1200,"vy":1400,"pet":"doro"}'
$X --ctl '{"ctl":"pointer","x":800,"y":90,"seconds":3}'   # 假光标（不动真鼠标），测追光标和 Dock 放大
$X --ctl '{"ctl":"scene","path":"/tmp/s.png","scale":1}'  # 窗口轮廓 + 角色、气泡、信封、点击波纹的真实渲染
$X --ctl '{"ctl":"menu"}'                                 # 打印菜单
```

针对单个角色的命令都可以加 `"pet":"<id>"`；不加时作用于第一个角色。

</details>

<details>
<summary><b>权限与隐私</b></summary>

- 跟随窗口只读窗口位置和所属 App 名，不读窗口内容和标题，不需要屏幕录制权限。
- 跟拍需要“系统录音”权限，不给也能跳，只是不跟拍。App 是临时签名（ad-hoc）构建的，每次重新构建后 macOS 可能会再问一次。
- 不联网，不开网络端口。

</details>

<details>
<summary><b>重新生成角色素材</b></summary>

```bash
python3 tools/gloria_sheet.py                                  # GLORIA：原画 + 平滑思考/舞蹈/操作电脑（需要 OpenCV）
DOROPET=~/Developer/DoroPet-macos $DOROPET_PYTHON characters/doro/render_doropet.py   # Doro（live2d-py + PyQt5）
$ANIMATED_DRAWINGS/.venv/bin/python characters/usagi/ad_usagi.py --build              # 乌萨奇（Animated Drawings）
/Applications/Blender.app/Contents/MacOS/Blender -b <grokbot_transform_v6.blend> --python characters/grokbot/export_grokbot.py   # GrokBot → grokbot.usdz
python3 tools/make_icon.py                                     # App 图标 → Resources/GLORIA.icns
python3 tools/readme_media.py [--cua 帧目录 框] [--grok3d 帧目录 框]   # 这份 README 里的图
make app
```

`DOROPET` 指向本机的 DoroPet-macos 目录（[DoroPet](https://github.com/waterfeet/DoroPet_V3) 的 macOS 版，默认 `~/Developer/DoroPet-macos`）；`ANIMATED_DRAWINGS` 指向 [AnimatedDrawings](https://github.com/facebookresearch/AnimatedDrawings) 的本地副本（默认 `~/Developer/animated-drawings-local`）。只是使用已经生成好的精灵图的话，这些都不需要。

另外，想导入 [dsh-pet](https://github.com/PC2005-cloud/dsh-pet) 桌宠可以跑 `python3 tools/import_dsh.py <dsh-pet 素材目录> --id <名字> --install`，它会把透明 `.webm` 视频抽帧转成 GLORIA 精灵图并装进 `~/Library/Application Support/GLORIA/characters/<名字>/`（附带 MIT `NOTICE.txt`），重开 GLORIA 后就能在菜单里选。

所有角色都用同一套格子：8 列、每格 192×208、脚踩 y=202，第 9–10 行是 16 个看的方向。动作的行号、帧数、每帧时长、锚点和朝向（`facing`）写在各自的 `<id>.json` 里，长动作可以跨行。GrokBot 的 JSON 用同样的行号和帧数，只是不对应图片，而是对应 `Sources/Model3D.swift` 里的姿势。

目录结构：`Sources/` 是 App 代码，`Resources/` 放图标、`Info.plist` 模板和 DMG 里的说明，`characters/<id>/` 放每个角色的成品和生成脚本，`tools/` 放工具脚本，`docs/` 放 README 用的图，`VERSION` 是版本号。

</details>

<details>
<summary><b>已知限制</b></summary>

- 委托只认命令行里直接写出的 `codex` / `claude` / `cursor-agent` / `dsh` 调用。套在脚本里间接调用的认不出来。
- Codex 的 computer use 坐标按窗口内的坐标换算到屏幕上。窗口截图被缩放过时，落点会有偏差。Claude Code 的桌面点击会在真实光标停下的地方泛起波纹。
- DSH 的钩子不带回复内容，所以 DSH 做完时她只说「做完啦」，不复述回复的第一句。
- `dsh-pet` 导入只转换 `.webm`。只有 `.mov` 素材的宠物要先转成 webm。
- 化身在工作或等批准时站着演，不坐也不睡。
- Dock 放大的抬升是按放大曲线估算的，不读取 Dock 里真实的图标位置（那需要辅助功能权限）。
- 窗口顶边离菜单栏太近时放不下她们，这时她们会站在屏幕底部。
- GrokBot 是实时 3D 渲染，放出来时 App 大约多用 5–8% 的 CPU（每秒最多画 30 帧，过渡靠插值）。
- 多显示器：在一台叠在 MacBook 上方的外接屏上试过（拖过去、扔过去、换屏、两块屏的四条边探头）。旁边连着另一块屏幕的那条边不能探头，因为她藏起来的身子会露到那块屏幕上。

</details>

## 素材与许可

代码（`Sources/`、`tools/`、各角色的生成脚本）以 [MIT](LICENSE) 授权。角色美术不在 MIT 范围内，各自的来源如下：

| 角色 | 来源 | 说明 |
| --- | --- | --- |
| GLORIA · 柔彩 | 作者的原创角色 | 精灵图保留所有权利 |
| Doro | [DoroPet](https://github.com/waterfeet/DoroPet_V3)（Apache-2.0）自带的 Live2D Doro 模型，离屏渲染成帧 | Doro 是网络同人形象；本仓库只含渲染出的图，不含模型文件 |
| 抽象乌萨奇 | 一张乌萨奇（《ちいかわ》，©ナガノ）毛绒玩偶的照片 | 同人二创，仅供个人非商业使用 |
| GrokBot | 作者的 Blender 工程，导出为 3D 模型（`grokbot.usdz`） | 机身上的标志是 xAI 的 Grok 标志 |

## Star History

<a href="https://star-history.com/#seasnakes/gloria-mac-pet&Date">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="https://api.star-history.com/svg?repos=seasnakes/gloria-mac-pet&type=Date&theme=dark">
    <img src="https://api.star-history.com/svg?repos=seasnakes/gloria-mac-pet&type=Date" alt="Star History Chart" width="600">
  </picture>
</a>

---

<sub>灵感来自 <a href="https://github.com/lachydotmcg/capri-desktop">Capri</a>，没有使用它的代码或美术。各角色素材的来源见<a href="#素材与许可">素材与许可</a>。</sub>

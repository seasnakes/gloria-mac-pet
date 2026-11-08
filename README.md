<div align="center">

# GLORIA · Mac 桌宠

**住在窗口上的小伙伴。她们会坐在窗沿上、跟着音乐跳舞，<br>还会把 Claude Code 和 Codex 正在干的活演给你看。**

<img src="docs/hero.gif" width="720" alt="Doro、GLORIA、乌萨奇站在窗口上跟着音乐跳舞">

原生 Swift · Apple Silicon · macOS 14.2+ · 不联网 · v0.4.0

</div>

---

## 三个角色，可以同时放出来

<img src="docs/cast.png" width="820" alt="三个角色的主要动作：站着、坐窗沿、思考、等批准、跳舞、打盹、被拎起来、看光标">

| 角色 | 素材 | 做法 |
| --- | --- | --- |
| **GLORIA · 柔彩** | 已验收的 Q 版手绘帧 | 跳舞和思考在关键帧之间补了光流过渡帧：只把最近的关键帧沿光流推过去，所以不叠影，每个像素都来自原画 |
| **Doro** | 本机 DoroPet-macos（基于 [DoroPet](https://github.com/waterfeet/DoroPet_V3)）的站立版 | Live2D 头 + 程序绘制的身体，用 DoroPet 自己的环境离屏逐帧渲染；待机带呼吸、轻微摇头和眨眼 |
| **抽象乌萨奇** | 一张毛绒照片 | Animated Drawings：骨骼绑定 + ARAP 网格，动作来自 BVH 重定向 |

菜单「角色」里勾选谁出场。几个角色站在一起时会互相打招呼，不会叠在同一个位置。

## Claude Code × Codex：两个化身互相传话

<img src="docs/together.gif" width="585" alt="乌萨奇（Claude Code）把信封扔给 Doro（Codex），Doro 干完活再扔回来">

在菜单里给 **Claude Code 化身** 和 **Codex 化身** 各挑一个角色，比如乌萨奇当 Claude、Doro 当 Codex：

- **各演各的**：接到任务跳一下，干活时摆思考动作，头顶小字幕显示正在读、改、跑什么。需要你批准时举牌提醒并一直挂着气泡；做完了会说出回复的第一句。
- **互相委托**：Claude 跑 `codex exec "…"` 时，Claude 的化身会走过去，把一个小信封扔给 Codex 的化身。Codex 干活期间，它的化身一直在忙；干完再把信封扔回来。Codex 跑 `claude -p`，或者任一方调用对方的 MCP，也算委托。
- **派出子代理**：身边会跑出一个小分身，子代理结束时挥手消失。

她们只看不动手，不会替你批准任何操作。

## 她们还会

| | |
| --- | --- |
| 🪟 **坐窗沿** | 坐在窗口顶边，窗口移动就跟着走，窗口关掉就掉下去，被别的窗口挡住会跳上去。在 Dock 和屏幕底部是站着的 |
| 🫳 **拎起来甩** | 揪着后颈拎起来会手脚乱扑腾；松手后按甩的速度飞出去，撞墙会反弹，摔重了还会晕 |
| 🎵 **跟着音乐跳** | 音乐 App 一出声就跳，一拍一个舞步。给了“系统录音”权限就能跟上真实节拍，声音只在内存里分析 |
| 🖱️ **追光标** | 光标在她站的地方晃，她会跑到光标旁边扑一下，不会钻到光标底下挡住点击。连戳 5 下会生气 |
| 🔍 **跟着 Dock 动** | 站在 Dock 上时，放大的图标会把她顶起来；有 App 启动、图标弹跳时，她也跟着蹦 |
| 💤 **打盹** | 3 分钟没碰键盘鼠标就会打盹，一动就醒 |

鼠标只有点在她们身上不透明的地方才会被拦住，别处照常点到下面的窗口。

## 安装

```bash
bash build.sh --install     # 构建并装进 /Applications，然后启动（需要 Xcode 或 Command Line Tools）
```

她们不占 Dock。菜单栏里有一排出场角色的小头像，右键角色本人也能打开同一个菜单。找不到她们时点「叫她们过来」。开机自启在菜单里的「登录时启动」。

<details>
<summary><b>菜单一览</b></summary>

- **角色**：勾选谁出场，可以多选
- **Claude Code 化身 / Codex 化身**：接入（装钩子）、由谁来当
- 需要批准时提示音 · 叫她们过来 · 大小 · 自己溜达 · 闲着会打盹 · 追光标 · 登录时启动
- 随音乐跳舞 · 跟着节拍 · 浏览器里的音乐也跳
- **演示**：跳舞 16 秒 / Claude Code 一轮工作 / Claude 请 Codex 帮忙 / 被扔出去 / 被拎起来晃一晃 / 打个盹 10 秒

右键某个角色时，菜单只显示她自己，并多一项「让她先休息（隐藏）」。

</details>

<details>
<summary><b>化身钩子会改哪些文件</b></summary>

接入时会改写 `~/.claude/settings.json` 或 `~/.codex/hooks.json`。只增删 GLORIA 自己的条目，其他设置和钩子都保留。改之前会把原文件备份到 `~/Library/Application Support/GLORIA/hook-backups/`；改完后文件会被重新排版，键按字母排序。

钩子命令是 `GLORIA_HUSK=1 '<GLORIA.app>/Contents/MacOS/GLORIA' --hook claude|codex`。它通过私有 Unix 套接字转发精简后的事件，耗时约 10 ms，不输出任何内容；GLORIA 没开时直接退出。挪动了 App 之后重新打开一次，钩子会自动指向新位置。Codex 第一次会问要不要信任这些钩子，选信任。

</details>

<details>
<summary><b>命令行与自检</b></summary>

```bash
X=/Applications/GLORIA.app/Contents/MacOS/GLORIA
$X --hooks claude on|off                                  # 同菜单开关
$X --ctl '{"ctl":"status"}'                               # 所有角色的状态（JSON）
$X --ctl '{"ctl":"show","id":"doro","on":true}'           # 放出 / 收起角色：gloria / doro / usagi
$X --ctl '{"ctl":"role","source":"codex","id":"doro"}'    # 指定化身
$X --ctl '{"ctl":"together"}'                             # 演示 Claude 请 Codex 帮忙
$X --ctl '{"ctl":"dance","seconds":12}'
$X --ctl '{"ctl":"play","anim":"perch","pet":"usagi"}'    # 让某个角色做某个动作
$X --ctl '{"ctl":"throw","vx":-1200,"vy":1400,"pet":"doro"}'
$X --ctl '{"ctl":"pointer","x":800,"y":90,"seconds":3}'   # 假光标（不动真鼠标），测追光标和 Dock 放大
$X --ctl '{"ctl":"scene","path":"/tmp/s.png","scale":1}'  # 窗口轮廓 + 角色、气泡、信封的真实渲染
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
python3 tools/gloria_sheet.py              # GLORIA：原画 + 平滑思考/舞蹈（需要 OpenCV）
/Users/seasnake/Developer/DoroPet-macos-env/bin/python characters/doro/render_doropet.py
/Users/seasnake/Developer/animated-drawings-local/.venv/bin/python characters/usagi/ad_usagi.py --build
python3 tools/readme_media.py [截图帧目录]   # 这份 README 里的图
bash build.sh
```

所有角色都用同一套格子：8 列、每格 192×208、脚踩 y=202。动作的行号、帧数、每帧时长和锚点写在各自的 `<id>.json` 里，长动作可以跨行。

</details>

<details>
<summary><b>已知限制</b></summary>

- 委托只认命令行里直接写出的 `codex` / `claude` 调用。套在脚本里间接调用的认不出来。
- Claude/Codex 工作或等批准时，她们站着演，不坐也不睡。
- 坐下和站起是直接切帧，中间没有过渡帧。
- Dock 放大的抬升是按放大曲线估算的，不读取 Dock 里真实的图标位置（那需要辅助功能权限）。
- 窗口顶边离菜单栏太近时放不下她们，这时她们会站在屏幕底部。
- 多显示器按逻辑支持，只在一台外接屏上试过。

</details>

---

<sub>灵感来自 <a href="https://github.com/lachydotmcg/capri-desktop">Capri</a>，没有使用它的代码或美术。Doro 和乌萨奇是同人形象，仅供个人使用。</sub>

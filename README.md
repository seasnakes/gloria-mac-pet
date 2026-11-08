# GLORIA · 柔彩 — Mac 桌宠

参考 [Capri](https://github.com/lachydotmcg/capri-desktop) 的思路，用原生 Swift（AppKit + Core Audio）给 GLORIA 做的 Mac 桌面伙伴。
没有使用 Capri 的代码或美术；角色是已验收的柔彩 Q 版精灵图：原 11 行（9 种状态 + 16 方向看光标），加上 2026-11-07 补画并验收的 4 行（窗沿坐、跳舞、睡觉、被拎起），来源 `Codex桌宠-柔彩GLORIA/extend-perch-dance-sleep-20261107/output/spritesheet-extended-v3.png`。

版本 0.3.0 · Apple Silicon · macOS 14.2+

## 运行

```bash
bash build.sh          # 生成 GLORIA.app（需要 Xcode 或 Command Line Tools）
open GLORIA.app
```

她只在菜单栏显示一个头像，不占 Dock。右键她、或点菜单栏头像打开菜单。

## 角色（一次一个，菜单「角色」里切换）

| 角色 | 素材来源 | 做法 |
| --- | --- | --- |
| GLORIA · 柔彩 | 已验收的 Q 版精灵图 + 补画的 4 行 | 手绘逐帧 |
| Doro | DoroPet-macos 的站立版：Live2D 头 `opendoro/models/Doro/Doro.model3.json` + `StandingDoro` 程序绘制的身体 | `characters/doro/render_doropet.py` 用 DoroPet 自己的 Python 环境离屏渲染：表情直接设参数（思考、星星眼、感叹号、黑脸、墨镜），16 方向看光标用 Live2D 头部角度，跳舞用动作库里的 dance 片段，下蹲用身体的落地压缩 |
| 抽象乌萨奇 | 你给的毛绒照片 `characters/usagi/source.png` | `characters/usagi/ad_usagi.py` 用 Animated Drawings（和 DoroPet 的 standing-prototype/animated-rig 同一套）：照片做成 AD 角色（`ad/texture.png`、`mask.png`、`char_cfg.yaml`，16 关节 + 头），ARAP 网格让手脚弯成弧线；跳跃、开合跳、Dab、Jesse 舞用 AD 的 BVH 动作重定向，其余（待机、走路、挥手、坐、睡、被拎）直接设关节角度再走同一个 ARAP |

三个角色用同一套 8×15 格布局，所以站窗沿、拖拽甩、跳舞、打盹、Claude Code 化身对谁都一样；台词各有一套。乌萨奇之前的纸片木偶版留在 `characters/usagi/legacy-puppet/`。Doro 和乌萨奇是同人形象，仅供个人使用。

## 她会做什么

**窗口栖坐 + 拖拽甩**
- 坐在窗口顶边上，腿垂在窗口前面；Dock 露出来时也会坐在 Dock 边上。窗口移动她跟着走，窗口晃动她会扶稳。
- 窗口关掉、最小化，她会掉下去，落到下面的窗口或屏幕底部；被别的窗口挡住，她会跳到挡住她的那个窗口上。
- 她排在所站窗口的正上一层，前面的窗口能自然地挡住她。
- 拎起来时她是被揪着后颈挂在光标下，手脚乱扑腾，像钟摆一样跟着甩；松手按甩出的速度飞出去，撞墙反弹，落地有挤压；摔重了会晕一下。
- 3 分钟没碰键盘鼠标，她会坐下打盹、冒 z；一动鼠标就醒（菜单可关）。
- 站在 Dock 上时，鼠标划过 Dock，放大的图标会把她顶起来（按系统 Dock 的图标大小和放大大小计算）；有 App 启动、Dock 图标在弹跳时，她跟着蹦三下。
- 光标在她所在的窗沿上晃，她会跑过去扑；连戳 5 下会生气一会儿（菜单「追光标」可关）。
- 单击摸头，双击跳一下。光标靠近时会转头看光标。空闲时会自己溜达、挥手，偶尔跳到旁边的窗口上。
- 鼠标只在她身上不透明的像素才会被拦住，其余地方照常点到下面的窗口。

**随音乐跳舞**
- 不需要任何权限：通过 Core Audio 看哪个 App 正在出声。Apple Music、Spotify、网易云、QQ 音乐、酷狗等音乐 App 一出声就跳。
- 跟拍（可选，需要“系统录音”权限）：用私有的 Core Audio 进程 tap 实时分析低频节拍，估出 BPM 后按拍子跳。声音只在内存里分析，不录、不存、不传。
- 浏览器（Chrome、Safari、B 站等）里的声音，只有检测到稳定节拍才跳，看视频、听人说话时不会乱跳。
- 用补画的 8 帧舞步，每拍走 4 帧；16 拍一个循环，最后 4 拍原地蹦 + 甩头 + 定格，飘出音符。没有跟拍时用 116 BPM。
- 实测：工程里的 128 BPM 配乐估出 128.0 BPM；TTS 语音的置信度保持在跳舞阈值以下。

**Claude Code / Codex 化身**（仿 Capri 的 Code/Husk 模式）

菜单里打开「Claude Code 化身」或「Codex 化身」，再重启对应工具。她只看，不替你批准任何东西。

| 事件 | 她 |
| --- | --- |
| 会话开始 | 挥手：“Claude 上线啦” |
| 你发了提示 | 跳一下：“Claude 接活了：「…」”，进入思考循环 |
| 读、改、跑、搜 | 小字幕：“在改 Pet.swift”“在跑 npm test” |
| 需要你批准 | 双手合十、隔一会儿跳一下提醒，气泡一直挂着：“Claude 需要你批准：运行 npm test”，可选提示音 |
| 工具失败 | 难过一下：“Claude 踩了个坑” |
| 派出子代理 | 身边跑出一个小分身，结束时挥手消失 |
| 压缩记忆 | 故障闪烁：“记忆压缩中……” |
| 完成 | 检查成果 → 挥手，气泡显示回复的第一句 |

开启时会改写 `~/.claude/settings.json` / `~/.codex/hooks.json`：只增删 GLORIA 自己的条目，其他设置和钩子保留；改之前把原文件备份到 `~/Library/Application Support/GLORIA/hook-backups/`。文件会被重新排版（键按字母排序）。
钩子命令是 `GLORIA_HUSK=1 '<GLORIA.app>/Contents/MacOS/GLORIA' --hook claude`，通过私有 Unix 套接字转发精简后的事件，约 10 ms，不输出任何内容，GLORIA 没开时直接退出。移动了 App 之后重新打开她，她会自动把钩子指向新位置。

## 菜单

叫她过来 · 大小（小/中/大/特大） · 自己溜达 · 闲着会打盹 · 随音乐跳舞 · 跟着节拍 · 浏览器里的音乐也跳 · Claude Code 化身 · Codex 化身 · 需要批准时提示音 · 演示（跳舞 16 秒 / Claude Code 一轮工作 / 被扔出去 / 被拎起来晃一晃 / 打个盹 10 秒） · 退出

## 命令行

```bash
X=GLORIA.app/Contents/MacOS/GLORIA
$X --hooks claude on|off                     # 同菜单开关
$X --ctl '{"ctl":"status"}'                  # 当前状态（JSON）
$X --ctl '{"ctl":"dance","seconds":12}'      # 跳一会儿
$X --ctl '{"ctl":"say","text":"你好"}'
$X --ctl '{"ctl":"throw","vx":-1200,"vy":1400}'
$X --ctl '{"ctl":"demo"}'                    # 演示一轮 Claude 事件
$X --ctl '{"ctl":"play","anim":"perch"}'     # 播放任一动作：perch/dance/sleep/dangle/wave/…
$X --ctl '{"ctl":"sleep","seconds":10}'     # 打个盹
$X --ctl '{"ctl":"hold","seconds":3}'       # 模拟被拎起来晃
$X --ctl '{"ctl":"character","id":"doro"}'   # 切换角色：gloria / doro / usagi
$X --ctl '{"ctl":"launch"}'                  # 模拟 Dock 图标弹跳
$X --ctl '{"ctl":"pointer","x":800,"y":20,"seconds":3}'   # 自检：假光标（不移动真实鼠标），测 Dock 放大和追光标
$X --ctl '{"ctl":"scene","path":"/tmp/s.png"}'  # 自检用：窗口轮廓 + 她和气泡的实际渲染
```

## 权限与隐私

- 窗口跟随只读窗口位置和所属 App 名，不读窗口内容和标题，不需要屏幕录制权限。
- 跟拍需要“系统录音”权限；不给也能跳，只是不跟拍。用临时签名（ad-hoc）构建，每次重新构建后 macOS 可能会再问一次。
- 不联网，不开网络端口。

## 已知限制

- Claude/Codex 在工作、或在等你批准时，她站着演，不坐也不睡；空闲时才坐下。
- 坐下/站起是直接切帧，没有过渡帧。
- Dock 放大的抬升是按放大曲线估算的，不读取 Dock 里真实的图标位置（那需要辅助功能权限）。
- 重新生成角色：Doro `/Users/seasnake/Developer/DoroPet-macos-env/bin/python characters/doro/render_doropet.py`；乌萨奇 `/Users/seasnake/Developer/animated-drawings-local/.venv/bin/python characters/usagi/ad_usagi.py --build`。然后 `bash build.sh`。
- 之前用 Blender 模型（experiments/blender-mochi）渲染的 3D 版 Doro 留在 `characters/doro/blender-3d/`，不在菜单里。
- 接近全屏的窗口顶边离菜单栏太近，放不下她，这时她站在屏幕底部（Capri 也是这样）。
- 只在单显示器上实测过；多显示器按逻辑支持，还没在真机上验证。
- 截图验证用的是 App 自己的渲染（`scene`），没有真实桌面截图。

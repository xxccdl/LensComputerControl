# Lens 电脑控制

零第三方依赖的 Windows 电脑控制插件。引擎是随包分发的 `bin/lens.exe`（C# 编写，用系统自带 `csc.exe` 编译，源码在 `native/`），只调用 Windows 自带的 Win32 / GDI+ / UIAutomation / WinRT OCR。

## 设置

装好之后 **设置** 面板左侧导航里会多出 **Lens 电脑控制** 这一项（和「通用设置 / 模型 / 内置插件」并列），改完点保存立刻生效（不用重启）：

| 设置项 | 默认 | 作用 |
|---|---|---|
| `hudEnabled` | 开 | 显示 HUD 蓝边与状态胶囊；关掉就完全不启动那个窗口 |
| `confirmClicks` | 开 | **坐标点击先预览、不落点**：先回一张绿框图 + `ck_xxx` 确认令牌，确认后才真正点击 |
| `humanize` | 开 | 人类化输入节奏（缓动 + 微抖动 + 按键间隔抖动）；关掉等于所有动作加 `--instant` |
| `autoCheck` | 关 | 预览关闭时的兜底：直接校验坐标，被遮挡或越界就拒绝点击（`id`/`cell` 不受影响） |
| `timeoutMs` | 25000 | 单次引擎调用超时（毫秒） |
| `checkBox` | 24 | `computer_check` 默认命中范围（像素，鼠标热区大小） |
| `checkZoom` | 8 | `computer_check` 放大镜倍率（2~16） |
| `blockedTitles` | 空 | **永不操作的窗口**：填标题关键词（如 `网银`、`支付宝`），命中即拒绝动作并返回 `WINDOW_BLOCKED` |
| `hudPort` | 45871 | HUD 本地端口，状态卡固定连 45871，非必要别改 |


## 核心思路

**模型永远不输出绝对坐标。** 引擎把窗口扫成一张「候选表」并给每个目标编号，模型只负责说「点 #17」；坐标由引擎从候选框精确算出。

这样做的原因：让多模态模型直接报像素坐标，误差通常在几十像素；而让它从编号里挑一个，等于把定位问题降级成分类问题，误差归零。

## 六个感知通道

| 通道 | 覆盖场景 |
|---|---|
| `uia` | 常规 Win32 / WPF / Electron 应用（白送像素级精度） |
| `ocr` | Windows 自带 OCR，原图 / 反色 / 2× 最近邻上采样三路 |
| `solid` | 量化颜色连通域，专抓实心 / 圆角按钮 |
| `table` | 行列投影，认行情软件的表格网格 |
| `textgrid` | 靠文字位置聚类行列，兜住暗色低对比、只画内线的自绘表 |
| `template` | 零均值 NCC 锚点，教一次永久精确 |

自绘窗口（微信 / QQ / 飞书 / 券商行情 / Java Swing）UIA 几乎是空的，靠后五个通道兜住。

## 工具

| 工具 | 作用 |
|---|---|
| `computer_windows` | 列窗口（标题、DPI、进程、真实可见边框、最小化状态） |
| `computer_scan` | 多通道扫描 → 候选表 + 带编号的标注图 |
| `computer_act` | 按候选 id / 表格行列 / 坐标点击，发键，输入中文，拖动 |
| `computer_verify` | 点完做像素差分，判断到底有没有生效 |
| `computer_grid` | 坐标网格：边缘刻度 + 二级放大，没有候选时也不猜像素 |
| `computer_vision` | **纯视觉递归定位**：截图切 N 格、每格标注屏幕绝对坐标，选一格后只切那一格，逐步收窄直到够准 |
| `computer_check` | **坐标检验**：把你打算点的坐标画成绿色框（默认 24×24 热区）+ 同处放大镜，回一张图；顺带报是否被遮挡、命中哪个候选 |
| `computer_scroll` | 滚轮：列表/聊天记录/网页/表格；真实滚轮事件，滚动前先置前并做遮挡检查 |
| `computer_run` | **脚本自动化**：一次调用执行多步（点击/输入/滚动/拖动/等待/核验），返回逐步 trace |

### 脚本自动化（computer_run）

把一串动作写成一个脚本，一次调用跑完，省掉 N 次往返：

```
window 微信
focus
scroll delta=-3 times=2
click find=text:张三
type text=在吗
key combo=enter
wait for=changed timeout=5000
verify expect=changed
```

支持的动词：`window` `focus` `click` `type` `key` `scroll` `drag` `wait` `verify` `sleep`。
默认遇错即停并返回 trace；`continueOnError` 可改成失败也继续。坐标可写 `w` 前缀表示相对窗口客户区（如 `from=w100,w100`）。

### 滚动（computer_scroll）

滚轮事件发给「光标当前所在」的窗口，所以引擎会先把光标移到目标位置（缓动），再逐格发送。

```
computer_scroll { window: "微信", delta: -3, times: 2 }
```

`delta` 为正 = 向上/向左，为负 = 向下/向右。给了 `window` 会先置前、并检查滚动点有没有被别的窗口遮挡——滚错窗口等于误操作，所以默认拒绝。

### 纯视觉递归定位（computer_vision）

当所有识别通道都失效时用这个——游戏、画布、远程桌面、自绘且无文字的面板。

1. `computer_vision {}` → 整屏切成 10 格（5×2），每格印着 `#编号`、`x,y WxH`、`@中心点`
2. 看图，报你要第几格 → `computer_vision {path:"7"}`
3. 只把那一格再切 10 份，继续选 → `{path:"7.4"}`
4. 够准了就 `computer_act {x: cx, y: cy}`

每一级的坐标都是**屏幕物理像素绝对值**，放大只影响看的清晰度，不影响坐标。默认按区域长宽比自动选接近正方形的行列数，也可以用 `cols`/`rows` 强制。

### 坐标检验（computer_check）

坐标是自己算的、从视觉格子里拿的、还是图里目测的？点下去之前先让它验一遍 —— 这会省掉一次误点。

```
computer_check { window: "微信", x: 412, y: 268 }
```

回一张图：目标点被画成**绿色框**（默认 24×24，即鼠标热区大小），带十字准星和 `坐标 + 尺寸 + 命中候选` 的标签；右下角贴一张同一处的**放大镜**：
最近邻放大（最高 16×）、叠 8px 像素格、绿框同样画进去。压住的是哪个字、是不是按钮边缘，看图就定了。

同时回 JSON：

| 字段 | 含义 |
|---|---|
| `inside` | 坐标在不在目标窗口内（`false` = 点跑到窗口外了） |
| `covered` / `coveredBy` | 该点此刻是否被别的窗口盖住，被谁盖住 |
| `pixel` | 该点像素色（判断是压在按钮上还是空白处） |
| `candidate` | 命中了上次 `computer_scan` 的哪条候选（含文字），能直接用 `computer_act {id}` |
| `box` | 实际命中矩形（`x1,y1,x2,y2`），尺寸可用 `w`/`h` 改 |

它**只读**：不移动鼠标、不发按键、不改窗口状态。

### 坐标点击先预览（confirmClicks）

`computer_act` 给 `x/y` 点击时，**默认不点**：

```
computer_act { window: "微信", x: 412, y: 268 }
→ { ok: true, clicked: false, code: "CLICK_NEEDS_CONFIRM",
    image: ".../preclick-xxx.png",      ← 绿框 = 会落点的范围（含放大镜）
    box: { w: 24, h: 24, x1, y1, x2, y2 },
    covered: false, pixel: "#2A2A2B", candidate: { id: 17, text: "发送" },
    token: "ck_3f9a1b7c" }
```

看图确认位置对了，再用同一个窗口 + 令牌落点：

```
computer_act { window: "微信", confirm: "ck_3f9a1b7c" }
```

- 点的是**令牌里记住的坐标**，不是这次传进来的 —— 防止"看的是 A、点的是 B"
- 令牌一次性、180 秒有效；过期或复用都返回 `CONFIRM_INVALID` / `CONFIRM_EXPIRED`
- 要换位置就重新给坐标，会重新出图并给新令牌
- `id` / `cell` 点击不预览（坐标本来就是引擎算的，不是模型的猜测）；确实要跳过预览时用 `force: true`
- `computer_run` 脚本里的 `click x= y=` 仍按脚本一次性执行（脚本本身就是编排好的流程）

### 视觉反馈层（HUD）

AI 操作电脑时，屏幕上会有一圈渐变蓝边，顶部浮一个胶囊：

```
● Lens AI 正在操作你的电脑 | 正在点击
```

胶囊右边那句话由引擎自动更新（每个命令映射一句人话），最后一条动作结束约 3.5 秒后淡出。

- 第一次调用任何 `computer_*` 工具时自动拉起 `bin/lenshud.exe`，不用手动开
- 命令行：`lens hud --action start|stop|status`，或 `lens hud --action say --text "正在导出"`
- **点击穿透**（`WS_EX_TRANSPARENT`），不会挡住你操作
- **对截屏不可见**（`WDA_EXCLUDEFROMCAPTURE`），所以不会污染 `computer_scan` 的候选表、也不会出现在 `computer_vision` 的分格图里
- 不占任务栏、不进 Alt+Tab

### 微信 / QQ 这类 Qt 自绘窗口

它们没有可用的 UIA（节点通常 <10），候选几乎全靠 OCR，所以有几个必须知道的坑：

- **OCR 会认错字**：老师→老丝、取消→取肖、语音通话→浯音通话。`find=text` 要用**短关键词**，别拿整句去精确匹配。
- **找联系人别翻列表**：用 `Ctrl+F` 打开搜索框（微信 4.x 实测有效），输入名字回车，比在会话列表里 OCR 匹配稳得多。见 `examples/wechat-send.txt`。
- **登录窗和主窗是两个不同 hwnd**：登录成功后登录窗销毁、主窗新建。脚本里用 `window 微信` 按标题定位，别缓存 hwnd。
- **点了登录按钮窗口消失 = 成功**：引擎返回 `warning:"window_gone"`，不是失败。
- **最小化到托盘 ≠ 已退出**：窗口变成隐藏状态，`computer_scan` 会找不到。先调 `computer_focus`（会自动还原并返回 `restoredFromTray:true`），**千万不要重新启动应用**——那会开第二个实例、弹登录页。

## 命令行

引擎也能单独用（`bin/lens.exe --help` 看全部）：

```
lens info
lens scan   --window "微信" --overlay out.png
lens click  --window "微信" --id 6
lens table  --window "Excel书" --json
lens fill   --window "XX" --plan plan.txt
lens grid   --window "XX" --cols 32 --rows 18
lens run    --flow examples/ppt-textbox.txt
```

坐标检验（同一个引擎，只读、不点不按）：

```
lens check --window "微信" --x 412 --y 268 --out check.png
lens check --desktop --x 960 --y 540 --w 32 --h 32 --zoom 12
```

## 多步流程

流程是行式脚本，可进 git、可 review、可重放：

```
window Workbench
focus
drag from=w220,514 to=w540,594 duration=700
type text=季度报告
assert text=季度报告
```

坐标支持 `w` 前缀表示「相对窗口客户区原点」，窗口挪位置不用改流程。
`examples/` 里有两个可直接跑的流程。

## 安全

- **急停**：`POST http://127.0.0.1:45871/stop` 会立刻在工具层拒绝所有 `computer_*` 调用。急停不依赖 UI 渲染。
- **遮挡拒绝**：目标点被别的窗口盖住时返回 `TARGET_COVERED` 并拒绝点击，不会误操作你正在用的窗口。

## 已知限制

- 反截屏窗口（`WDA_EXCLUDEFROMCAPTURE`）下截图返回全黑，只能做引导不能代点。
- AWT/Swing 窗口不响应后台 `PostMessage` 点击，必须前台真点击。
- 填表的自动回读依赖 OCR，部分短词读不出，此时以「写入成功」为准（可用应用自身状态或差分复核）。
- 目前只支持 Windows。

## 许可

MIT

# Lens 已知坑与坐标约定（lens-quirks）

> 这份文档来自一次真实的多步操作复盘：一个只有纯视觉通道可用的窗口（自绘 UI），
> 从"看图 → 定坐标 → 点击 → 核对"整条链路走下来，踩到的坑按危害排序。
> 每条都标了**状态**：已修 / 有规避手法但未修 / 设计如此。

## 一、坐标系约定（先读这段，能省掉一半坑）

| 位置 | 坐标系 | 说明 |
|---|---|---|
| `computer_scan` 的 `screen{cx,cy}` | 屏幕绝对坐标 | 引擎从候选框算的，**最可信** |
| `computer_vision` 格子里的 `x,y WxH @cx,cy` | 屏幕绝对坐标 | 放大只改可读性，不改坐标 |
| `computer_check` 的 `x/y` | 屏幕绝对坐标 | 绿框中心就是你给的点 |
| `computer_act` 的 `x/y` | 屏幕绝对坐标 | 先预览，确认后才落点 |
| `computer_look` `mode:"image"` 的 `x,y` | 屏幕绝对坐标 | 图内 `(px,py)` → 屏幕 `(x+px, y+py)` |
| `computer_look` `mode:"text"` 的 `rect` | **由 `space` 决定** | 默认 `auto`：像屏幕坐标就按屏幕坐标换算；返回 `rectSpace` 告诉最终按哪套算的 |
| `computer_window` 返回的 `frame` | 屏幕绝对坐标（可见边框） | 不含 DWM 阴影；**做换算用它，别用你请求的值** |

**预览图会被缩放。** `read_image` 打开标注图时可能把长边压到 ~1500–1700px，图内像素 ≠ 屏幕像素。
坐标一律读图上印的数字（每格都标了绝对坐标），不要用肉眼估比例 —— 这条最容易错，错了还不报错，只是点到别处。

## 二、按危害排序的坑

### 1. `look/image` 的 `rect` 被忽略、`look/text` 的 `rect` 是客户区坐标 —— **已修**

- 复现：`computer_look {mode:"image", rect:"300,660,1000,90"}` → 仍返回整窗 `1885x902`。
- 复现：`computer_look {mode:"text", rect:"320,800,1300,150"}`（按屏幕坐标给）→ 回报"区域 1300x150"但只读出一个 `9`（越界截到窗口底边）。
- 根因：引擎 `cap` 完全没有解析 `--rect`（只做 DPI 内容裁剪）；`read --rect` 一直是客户区相对。
- 修复：
  - 引擎 `cap` 现在支持 `--rect x,y,w,h`（屏幕坐标），桌面截图与窗口截图两条路径都裁剪，返回的 `x,y,w,h` 是裁剪后的真实原点；
  - `look` 新增 `space` 参数（`auto`/`screen`/`client`），`text` 模式会按 `space` 换算并在返回里带 `rectSpace`、`rectScreen`；
  - `image` 模式把 `rect` 传下去，并在返回里写明"这张图只包含该区域"。

### 2. 绝对屏幕坐标会"过期"，且视觉流拿不到指纹 —— **部分已修**

- 复现：`computer_vision` 在窗口 `@16,0` 时把侧边栏标成 `@43,360`；等窗口被挪到 `@28,20` 再 `check(43,358)`，放大镜里是纯背景色。
- 已修：`computer_vision` 返回里现在带 `fingerprint`（取最近一次同窗口 scan 的指纹）和 `staleHint`，可以直接喂给 `computer_act` / `computer_check` 的 `expectedFingerprint`，界面一变就被拒。
- 未修：`expectedFingerprint` 仍然只在同一窗口的 scan 之后才有值。**没有 scan 过的窗口，视觉坐标无法自动失效检测**——这时只能靠"同一条消息里 vision → act 之间不插别的事"来降低窗口移动的概率。

### 3. `verify` 的像素差分把"悬停高亮"当成功 —— **已修（措辞 + 提示）**

- 复现：`computer_act(55,395)` 回"画面有变化，动作生效"，实际目标纹丝没动（只是 hover 高亮改了像素）。
- 修复：`changed=true` 不再说"动作生效"，改成明确提示"界面重绘过 ≠ 点对了：悬停高亮、光标闪烁都会让它变"，并建议用 `look` 看内容或 `scan` 看有没有出现预期候选。
- 仍未解决：像素差分本身没有语义。**`verify` 只当"可能变了"的信号，别当判据。**

### 4. `move` / `info` 的几何有固定偏差 —— **已修（自动校准）**

| 请求 | 修复前回报 |
|---|---|
| `20,20,1184,1002` | `28,20,1168,994` |
| `8,0,1200,1010` | `16,0,1184,1002` |

规律：x 稳定 +8、w −16、h −8（不可见的调整边框）。
修复：`computer_window` 的 `move` 现在**按回报值迭代校准**（最多 3 轮，±2px 收敛），返回里带 `requested`、`frame`（真实值）、`corrections`、`geometryNote`。
实测：请求 `200,200,900,600`，回报 `200,200,900,600`。

另有一例 `1885x902 @8,90` 突然变成 `960x1020 @0,0`，未能复现，怀疑是系统贴靠（Snap Assist）介入；校准循环对这种情况会在 `corrections` 里露出多个回合，可作为线索。

### 5. 可见性判定与真实可操作性脱钩 —— **已修（可捕获性探测）**

- 复现：窗口不在 `computer_windows` 列表里；`computer_act` 报 `TARGET_COVERED`（先说被 919148 挡，focus 后又说被 199778 挡）；但同一时刻 `PrintWindow` 能截出完整画面、`background` 点击也能点。
- 已修：`computer_act` 的遮挡检查不再一律硬拒 —— 当 `background:true` 或 `force:true` 且点落在窗口内时，遮挡降级为 warning 继续执行；报错文案里也明确提示"该窗口不该在前台时改用 background"。
- 已修：`computer_windows { probe: "微信" }` 会真抓一帧并回 `capturable` / `captureMethod` / `blackRatio`；
  `computer_act` 因遮挡被拒时也会自动 probe 一次，hint 里分两种情况：
  「抓得到 → 只是不在最前面，用 `background:true`」/「抓不到 → 反截屏保护，只能引导」。
- 仍未做：把 `capturable` 缓存进窗口列表（现在每次 probe 都是真抓一帧，所以只在需要时才调）。

### 6. 我自己踩的坑（设计如此，但要记住）

`read_image` 的预览是缩放过的（1920x1080 渲染成 1708x961，约 1.124×）。我按预览目测的输入框位置点下去，落到消息列表里，粘贴的文本直接丢了 —— 连错两次才靠 OCR 兜回来。
结论见第一节：**坐标只信图上印的数字**。

## 三、当前推荐的规避顺序

1. `move` 把窗口钉死 → 紧接着 `info`，**用回报的 `frame` 当基准**（现在它和请求一致了）。
2. 有文字的控件优先 `computer_scan`：它的 `screen{cx,cy}` 实测最准。
3. 纯视觉流程：`vision` 出坐标 → **同一条消息里立刻 `act`**，中间不插拍照/OCR；把 `fingerprint` 带上。
4. `verify:true` 只当"可能变了"；真判据是下一次 `look` 看到的内容。
5. 拿不准先 `check`：它直接告诉你 `covered:true` 和命中了哪个候选。
6. 托盘/后台窗口别纠结 `TARGET_COVERED`，直接 `{background: true}`。

## 四、还没修的（按优先级）

| # | 问题 | 影响 | 建议方向 |
|---|---|---|---|
| 1 | `expectedFingerprint` 对未扫描窗口无效 | 视觉坐标过期无检测 | 让 `vision` 自己算一次轻量指纹（窗口矩形 + 标题 + 时间戳）并回传 |
| 2 | ~~无 `capturable` 字段~~ | **已修**：`computer_windows { probe: "关键词" }` 与 `computer_act` 的遮挡错误都会回 `capturable` / `captureMethod` / `blackRatio` | — |
| 3 | `verify` 无区域语义 | 悬停误判 | 差分时排除光标周围 ±20px，或只比较落点外的区域 |
| 4 | 窗口被系统贴靠改写几何 | 偶发尺寸突变 | 校准失败时回传 `snapSuspected: true` |

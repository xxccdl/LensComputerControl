# 变更记录

版本号规则：`0.x` 阶段按「新增平台 / 破坏性改动 = 次版本」，其余补丁位。

## 0.10.0

### 新平台

- **macOS（实验性）**：新增零依赖 Node 引擎 `engine-mac.mjs`，`index.js` 按 `process.platform` 自动切换，
  工具层不分叉。命令与 Windows 侧同构：`info / scan / read / check / vision / cap / probe / click / type / key /
  paste / focus / window / scroll / drag / verify / run`。
- macOS 的 OCR 走三级回退：已编译的 `bin/ocr-mac` → 系统有 `swiftc` 时自动编译随包的
  `native/mac/ocr-mac.swift`（Vision 框架，约 120 行）→ 名为 `LensOCR` 的快捷指令。
- macOS 侧没有原生浮层（`lenshud.exe` 是 Win32 实现），HUD 只在 Web 状态卡里。

### 新增能力

- **坐标检验**（`computer_check`）：给坐标回一张绿框（命中范围）+ 右下角放大镜的截图，并回
  `inside / covered / coveredBy / pixel / candidate`。
- **坐标点击先预览**（`confirmClicks`，默认开）：`computer_act {x,y}` 不落点，先回预览图 + `ck_` 一次性令牌，
  `{confirm:"ck_xxx"}` 才真点；落点取自令牌内坐标。
- **切块递归定位**（`computer_vision`）：区域切格、每格印编号与屏幕绝对坐标，`path:"7.4"` 逐级下钻。
- **可捕获性探测**：`computer_windows { probe }` 与 `computer_act` 的遮挡错误会回
  `capturable / captureMethod / blackRatio` —— 区分「窗口不在最前面」与「反截屏保护抓不到」。
- **HUD 与设置面板**：HUD 卡片按 `READY / L{级} / STOPPED / OFFLINE / NO PERM` 显示状态，
  配色走宿主设计令牌（浅色主题自适应）；设置页新增「引擎诊断」块（平台 / 引擎 / HUD 形态 / 权限 / 最近扫描）。

### 修复（全部来自真实复现，明细见 `docs/lens-quirks.md`）

- `look` 的 `rect` 之前被引擎完全忽略；`cap` 现在支持 `--rect`（桌面与窗口两条路径），
  `look` 新增 `space: auto|screen|client`。
- `verify` 的像素差分把「鼠标悬停高亮」当成动作成功（假阳性）：`changed=true` 不再等于「生效」，
  小块变化会标 `hoverSuspected`。
- `computer_window` 的 `move` 几何有固定偏差（x+8 / w-16 / h-8）：改为按回报值迭代校准（±2px 收敛）。
- 遮挡一律硬拒：`background:true` / `force:true` 且点在窗口内时降级为 warning。
- 引擎输出带 UTF-8 BOM 时 `JSON.parse` 直接抛错：`parseLoose` 现在先剥 BOM。
- 八个工具的 description 重写，体积 4,664 → 2,234 字符（约省 1.4k token），决策顺序提到第一行。

### 工程

- `npm test`：`tools/test-all.mjs` 逐个独立进程跑 `tests/test-*.mjs`，当前 19 个文件 / 135 项断言。
- 测试目录以前在 `.gitignore` 里（仓库从没带上过测试），现已纳入版本管理。
- `tools/build-lens.ps1` 输出目录改回仓库根 `bin/`；`native/` 快照与 `src/` 保持同步。

## 0.8.7

- 首个公开发布：Windows 引擎（六通道识别、坐标检验、批量填表、流程脚本）、HUD、设置面板。

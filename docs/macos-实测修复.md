# macOS 实测问题修复清单（对应 2026-11-22 报告）

> 目标版本：0.10.1（未发布）。逐条对应报告编号，标注**已修 / 部分修 / 未修**。
> 本机是 Windows，下面所有"已修"都经过 `node --check`、单元测试与代码级复核，
> 但**没有在真机 macOS 上复跑**——请按文末的复验清单再跑一遍。

## P0（阻断级）

| 编号 | 问题 | 状态 | 改法 |
|---|---|---|---|
| P0-1 | 按 id 点击 `TypeError: Assignment to constant variable` | **已修** | `cmdClick` 的 `x/y` 从 `const` 改为 `let` |
| P0-2 | 坐标点击 -1700（`se.click({at})` 不可用） | **已修** | 改用 `CGEventCreateMouseEvent` + `CGEventPost(kCGHIDEventTap)`，down/up 成对，支持左右中键与 `--count` |
| P0-3 | 预览结果 `not lossless JSON` | **已修** | 新增 `toJsonSafe()`：Buffer→`[buffer NB]`、NaN/Infinity→null、undefined/函数剔除；`runExe` 所有解析结果统一过一遍 |
| P0-4 | `computer_vision` 不可用；`grid` 打印 USAGE | **已修** | 引擎补 `grid` / `zoom` 子命令（轴刻度 + 二级网格），返回 `screenCellW/H`、`origin`；vision 结果同样过 `toJsonSafe` |
| P0-5 | OCR 永远不可用（`URL.pathname` 的 `%20`） | **已修** | `thisDir()` 改用 `fileURLToPath(import.meta.url)`，两处调用点都换成它 |
| P0-6 | `look text` 不给 rect 必失败 | **已修** | 引擎把 `0,0,99999,99999` 或缺失视为"整个区域"，不再算越界 |
| P0-7 | `read --rect` 坐标系颠倒 | **已修** | `read` 的 rect 一律按**屏幕坐标**解释（与 `cap` 一致），越界时回 `hint` 带窗口 origin |
| P0-8 | 组合键/粘贴 -1700 | **已修** | `key` 全量改 CGEvent（keycode 表 + modifier flags）；`paste` 的 Cmd+V 也走 CGEvent |
| P0-9 | `scroll` 假成功（`PostToPid(0)`） | **已修** | 改为 `CGEventPost(kCGHIDEventTap)`，并先把光标移到目标点再滚 |
| P0-10 | 脚本 `click find=text:` 不点击 | **已修** | 命中候选后真的发 down/up（复用 `mouseScript`） |
| P0-11 | `scan` 22–49s 撞工具层超时 | **部分修** | AX 遍历限流（深度 12→8、节点 400→150、少取 `description`）；超时现在明确回 `ENGINE_TIMEOUT` 并给出"调大超时/改用切块"的建议。**未做**：批量取属性（要换 API），性能仍需真机复测 |
| P0-12 | HUD 永远 READY | **部分修** | `notePermission` 放宽到 `JXA/osascript/-1700/EACCES/ENOENT/ENGINE_TIMEOUT`；引擎失败不再静默（见 P0-13）。**未做**：把"每次失败"计数直接画成红点 |
| P0-13 | 异常被吞成 `[]` | **已修** | `listWindows` / `axCandidates` 记录 `lastError` 并在 `scan.notes2` 里外露；AX 为空会写明"未授权或不暴露无障碍树" |

## P1（功能缺陷）

| 编号 | 问题 | 状态 | 改法 |
|---|---|---|---|
| P1-1 | 遮挡/越界探测恒 true/false | **已修** | `windowAtPoint` 的 JXA 只保留一个 `out()`（原来 `out(obj)` 后又 `out(null)`，前者被丢弃） |
| P1-2 | `window move` 返回 undefined | **已修** | 动作后回读 frame；读不到就用请求值兜底，`info` 也补上 `frame` |
| P1-3 | minimize/restore -1700 | **已修** | 改为先判 `p.windows.length` 再设 `minimized`（原写法在窗口列表为空时抛异常） |
| P1-4 | maximize 未实现 | **已修** | 用 `NSScreen.visibleFrame` 铺满可见区域 |
| P1-5 | focus 只认进程名；失败被忽略 | **已修（引擎侧）** | 新增 `resolveProcess()`：进程名 → 窗口标题 → 模糊；找不到回 `WINDOW_NOT_FOUND` |
| P1-6 | `computer_run` 渲染 undefined、失败不报 | **部分修** | 超时/失败现在有明确 code；渲染字段仍是旧结构，待下一轮改成读 `steps`/`trace` |
| P1-7 | `action=move` / `cell` 不可用 | **部分修** | `move` 缺 x/y 时回明确的 `BAD_ARGS` + 示例；`cell` 仍不支持（属 P1 未做项） |
| P1-8 | 一批参数被静默忽略 | **部分修** | 新增 `--excludePid`（宿主窗口不再可被操作）、`scan --filter`、`info --probe`、`--button`/`--count`。**未做**：`--bg`、`--expectFingerprint` |
| P1-9 | drag 位移不符 | **已修** | 步数按 duration/16 计算（8–40 步）、每步补 `deltaX/deltaY`、每步 `usleep(8ms)`、按下/抬起走 HID tap |
| P1-10 | hwnd 伪造且两处不一致 | **已修** | `pickWindow` 认数字 hwnd（`pid*1000` 或 `pid*1000+i`）；`window info` 也回 `hwnd` |
| P1-11 | scan 与 click --id 编号空间不同 | **已修** | 抽出 `collectCandidates()`，scan 与 click --id 共用同一份（AX + OCR 混编），click 取 `screen.cx/cy` |
| P1-12 | `wait` 两套语义 | **部分修** | 脚本里的 wait 在 trace 里标注"近似：未真正等条件"；CLI 仍返回 NOT_IMPLEMENTED |
| P1-13 | paste 竞态 + pbcopy 不检查退出码 | **已修** | `pbcopy` 返回是否成功；Cmd+V 后等 180ms 再还原剪贴板 |
| P1-14 | probe 把失败包装成"反截屏保护" | **已修** | 抓取失败回 `captureReason:"capture-failed"` + `captureError` |

## P2（次要/安全）

| 编号 | 状态 | 说明 |
|---|---|---|
| P2-1 | **未修** | HUD 的 `/confirm`、`/stop` 仍是 `Access-Control-Allow-Origin:*`。需要加 HUD 令牌与来源校验，属下一轮 |
| P2-2 | **未修** | scan overlay 写盘失败被吞 |
| P2-3 | **未修** | `check --zoom` 仍夹到 8（设置页允许 16） |
| P2-4 | **未修** | `dpi` 硬编码 144；Retina 上 `cap` 的 x/y 与 w/h 单位不一致 |
| P2-5 | **未修** | `timeoutMs` 只在 apply 时读一次 |
| P2-6 | **未修** | 脚本 `type text=hello world` 未加引号只输入 `hello` |
| P2-7 | **已修** | 未知子命令不再打印 USAGE 而是回 JSON 错误（`cmdUnsupported` + dispatch 默认分支回 `UNKNOWN_COMMAND`）——见下方"顺手" |

## 顺手改的

- `parseCombo()` 导出并单测：`enter` 之前映射到 76（小键盘回车），已改为 36（主回车），另留 `kp_enter: 76`。
- `toJsonSafe()` 导出为 `toJsonSafeForTest`，单测覆盖 Buffer/NaN/undefined/函数/深层嵌套。
- 新增 `tests/test-mac-p0.mjs`（13 项）：组合键解析 + 可序列化清洗。

## 真机复验清单（请在 Mac 上按顺序跑）

```bash
cd <插件目录>
# 1. OCR 路径（P0-5）：应打印可选路径，而不是 OCR_UNAVAILABLE
node engine-mac.mjs read --window Calculator --rect 100,120,200,200

# 2. 点击（P0-1/P0-2）：先 scan 拿 id，再按 id 点；再试坐标点
node engine-mac.mjs scan --window Calculator
node engine-mac.mjs click --window Calculator --id 3
node engine-mac.mjs click --x 300 --y 300 --button right

# 3. 组合键（P0-8）
node engine-mac.mjs key --combo "cmd+a"
node engine-mac.mjs paste --text "hello 世界"

# 4. 滚动（P0-9）：滚动前后截图差分应明显大于噪声
node engine-mac.mjs scroll --delta -5 --x 500 --y 500

# 5. 拖动（P1-9）：窗口位移应接近请求值
node engine-mac.mjs drag --from 300,215 --to 550,400 --duration 600

# 6. 遮挡探测（P1-1）：点在窗口外应 inside:false
node engine-mac.mjs check --window Calculator --x 5 --y 5

# 7. vision / grid（P0-4）
node engine-mac.mjs vision --window Calculator --cells 9 --out /tmp/v.png
node engine-mac.mjs grid --window Calculator --out /tmp/g.png

# 8. scan 耗时（P0-11）
time node engine-mac.mjs scan --window "System Settings"
```

任何一条仍失败，把命令 + 输出贴回来，我按编号继续收。

import { execFile } from 'node:child_process'
import { mkdirSync, readFileSync, readdirSync, statSync } from 'node:fs'
import { createServer } from 'node:http'
import { tmpdir } from 'node:os'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'

/**
 * Lens —— 零第三方依赖的电脑控制。
 *
 * 设计铁律：模型永远不输出绝对坐标。坐标只来自本地引擎 lens.exe 算出的候选框，
 * 模型的工作只是「选编号」。这样自绘窗口（微信/QQ/券商/Java 客户端）也能精确点击。
 */

/** 本包目录：引擎二进制随包分发，默认从这里解析，不依赖安装机器的任何绝对路径。 */
const HERE = dirname(fileURLToPath(import.meta.url))

const text = (s) => [{ type: 'text', text: s }]

/** Cordis plugin identity. */
export const name = 'lens-computer-control'
/** We only need the tool registry. */
export const inject = ['tools']

/**
 * 设置项：DSH 的「设置 → 插件 → Lens」表单是用这里的 Config schema 自动生成的，
 * 改完立刻落到 profile 的插件配置里，下次挂载就按新值跑。
 *
 * 宿主会为 profile 插件兜底解析 `@deepseek-ai/*`（resources/host-module-fallback.mjs），
 * 所以这里动态导入 schemastery：万一拿不到（比如脱离宿主直接 import 本文件做测试），
 * 就退化成"没有设置表单"，绝不因为一个可选依赖让整个插件加载失败。
 */
let z = null
try {
  const mod = await import('@deepseek-ai/schemastery')
  z = mod.default ?? mod
} catch {
  z = null
}

const SETTING_FIELDS = {
  hudEnabled: {
    schema: () => z.boolean().default(true),
    text: '显示 HUD 蓝边与状态胶囊 / Show the HUD overlay',
  },
  humanize: {
    schema: () => z.boolean().default(true),
    text: '人类化输入节奏（缓动 + 微抖动 + 按键间隔抖动）/ Humanized input timing',
  },
  confirmClicks: {
    schema: () => z.boolean().default(true),
    text: '坐标点击先预览不落点：返回带绿框的图，模型确认后才真正点击 / Preview coordinate clicks before they land',
  },
  humanConfirm: {
    schema: () => z.boolean().default(false),
    text: '点击必须由人工确认：模型不能自己确认，只有界面上的按钮（或你）能放行 / Require a human to confirm clicks',
  },
  autoCheck: {
    schema: () => z.boolean().default(false),
    text: '（预览关闭时的兜底）自动检验坐标，被遮挡就拒绝点击 / Auto-verify when preview is off',
  },
  timeoutMs: {
    schema: () => z.number().min(2000).max(120000).default(25000),
    text: '单次引擎调用超时（毫秒）/ Engine call timeout (ms)',
  },
  checkBox: {
    schema: () => z.number().min(8).max(200).default(24),
    text: '坐标检验默认命中范围（像素，鼠标热区）/ Default click box (px)',
  },
  checkZoom: {
    schema: () => z.number().min(2).max(16).default(8),
    text: '坐标检验放大镜倍率 / Magnifier zoom',
  },
  blockedTitles: {
    schema: () => z.array(z.string()).default([]),
    text: '永不操作的窗口（标题关键词，防止误点网银/聊天等）/ Window titles to never touch',
  },
  hudPort: {
    schema: () => z.number().min(1024).max(65535).default(45871),
    text: 'HUD 本地端口（状态卡固定连 45871，非必要别改）/ HUD local port (advanced)',
  },
}

// 每个字段都要标成 volatile：DSH 的 Loader 只把 volatile 字段当成「设置页可改」的项，
// 非 volatile 的 Config 只会被当成静态配置，设置命名空间根本不会出现在宿主的列表里
// （dsh-free-search / dsh-computer-use-win 都是这么标的）。
function volatileField(schema) {
  if (typeof schema.volatile === 'function') return schema.volatile()
  // 老一点的 schemastery 没有便捷方法时，用等价的元数据写法
  return schema.extra('volatile', true)
}

export const Config = z
  ? z.object(
      Object.fromEntries(
        Object.entries(SETTING_FIELDS).map(([key, field]) => [
          key,
          volatileField(field.schema().description(field.text)),
        ]),
      ),
    )
  : undefined

const SETTING_DEFAULTS = {
  hudEnabled: true,
  humanize: true,
  confirmClicks: true,
  humanConfirm: false,
  autoCheck: false,
  timeoutMs: 25000,
  checkBox: 24,
  checkZoom: 8,
  blockedTitles: [],
}

/** schemastery 可能把值包成 live reference（{get()}），两种形状都要吃得下。 */
function unwrap(value, fallback) {
  let v = value
  try {
    if (v && typeof v.get === 'function') v = v.get()
  } catch {
    v = undefined
  }
  return v === undefined || v === null ? fallback : v
}

/** 把插件配置规范化成运行时设置。
 *
 * 字段在 Config 里标了 volatile，宿主交给 apply 的会是 live reference（{get()}），
 * 设置页改完直接就地生效、不重新挂载插件。所以这里用 getter 每次都现读，
 * 而不是在 apply 时拷一份快照 —— 否则改完设置得重启才生效。 */
function readSettings(config) {
  const raw = config && typeof config === 'object' ? config : {}
  const pick = (key) => unwrap(raw[key], SETTING_DEFAULTS[key])
  const api = {}
  const define = (key, normalize) => {
    Object.defineProperty(api, key, { enumerable: true, get: () => normalize(pick(key)) })
  }
  define('hudEnabled', (v) => v !== false)
  define('humanize', (v) => v !== false)
  define('confirmClicks', (v) => v !== false)
  define('humanConfirm', (v) => v === true)
  define('autoCheck', (v) => v === true)
  define('timeoutMs', (v) => (Number(v) > 0 ? Number(v) : SETTING_DEFAULTS.timeoutMs))
  define('checkBox', (v) => (Number(v) >= 8 ? Number(v) : SETTING_DEFAULTS.checkBox))
  define('checkZoom', (v) => (Number(v) >= 2 ? Number(v) : SETTING_DEFAULTS.checkZoom))
  define('blockedTitles', (v) => {
    const list = Array.isArray(v) ? v : String(v || '').split(',')
    return list.map((x) => String(x).trim()).filter(Boolean)
  })
  return api
}

function pretty(value) {
  try {
    return JSON.stringify(value, null, 2)
  } catch {
    return String(value)
  }
}

/** 把报错里的窗口对象渲染成人话，让"被谁挡了/目标是哪个"一眼可见。 */
function DescribeWindowInfo(w) {
  if (!w) return "(未知)"
  return `${w.title || "(无标题)"} [hwnd=${w.hwnd} ${w.process || ""} ${w.frame ? w.frame.w + "x" + w.frame.h + " @" + w.frame.x + "," + w.frame.y : ""}]`
}

function describe(w) {
  if (!w) return '(未知)'
  const vis = w.visible === false ? ' 不可见' : ''
  return `${w.title || '(无标题)'} [hwnd=${w.hwnd} ${w.process || ''}${vis}]`
}

/** 容错解析：先整体 parse，失败则截取第一个平衡的 {…} 再试。
 *  实测「点了登录按钮后窗口立刻销毁」时 stdout 可能夹杂半截输出，一刀切 JSON.parse 会把
 *  一次成功的动作报成失败（B3）。 */
/**
 * 工具返回值要先能过宿主的 "lossless JSON" 校验：
 * Buffer → 只留长度，undefined/function 直接丢，NaN/Infinity → null。
 * macOS 实测里 computer_act 的预览与 computer_vision 都栽在这上面（P0-3 / P0-4）。
 */
function toJsonSafe(value, depth = 0) {
  if (value === null || value === undefined) return value === undefined ? undefined : null
  if (depth > 8) return '[deep]'
  const t = typeof value
  if (t === 'string' || t === 'boolean') return value
  if (t === 'number') return Number.isFinite(value) ? value : null
  if (t === 'function' || t === 'symbol' || t === 'bigint') return undefined
  if (Buffer.isBuffer(value)) return `[buffer ${value.length}B]`
  if (Array.isArray(value)) {
    return value.map((v) => {
      const safe = toJsonSafe(v, depth + 1)
      return safe === undefined ? null : safe
    })
  }
  const out = {}
  for (const k of Object.keys(value)) {
    const safe = toJsonSafe(value[k], depth + 1)
    if (safe !== undefined) out[k] = safe
  }
  return out
}

/** 仅供测试：把内部清洗函数暴露出去。 */
const toJsonSafeForTest = (v) => toJsonSafe(v)

function parseLoose(text) {
  // 引擎重定向输出时常带一个 UTF-8 BOM，JSON.parse 会直接抛错 —— 先剥掉
  const t = String(text || '').replace(/^\uFEFF/, '').trim()
  if (!t) return null
  try {
    return JSON.parse(t)
  } catch {
    /* 继续尝试截取 */
  }
  const start = t.indexOf('{')
  if (start < 0) return null
  let depth = 0
  let inStr = false
  let esc = false
  for (let i = start; i < t.length; i++) {
    const ch = t[i]
    if (inStr) {
      if (esc) esc = false
      else if (ch === '\\') esc = true
      else if (ch === '"') inStr = false
      continue
    }
    if (ch === '"') inStr = true
    else if (ch === '{') depth++
    else if (ch === '}') {
      depth--
      if (depth === 0) {
        try {
          return JSON.parse(t.slice(start, i + 1))
        } catch {
          return null
        }
      }
    }
  }
  return null
}
/**
 * 平台 → 引擎调用方案。
 * Windows: 随包的 bin/lens.exe（C# / Win32 + UIA + WinRT OCR）。
 * macOS:   随包的 engine-mac.mjs，用当前 Node 执行（Electron 下靠 ELECTRON_RUN_AS_NODE 转 Node 语义）。
 * 抽成纯函数是为了能在非 macOS 机器上直接测这段判断。
 */
export { toJsonSafeForTest }

export function enginePlan(platform, here, cfgExePath) {
  if (platform === 'darwin') {
    const script = join(here, 'engine-mac.mjs')
    return {
      engine: 'node',
      cmd: process.execPath,
      pre: [script],
      env: Object.assign({}, process.env, { ELECTRON_RUN_AS_NODE: '1' }),
    }
  }
  return {
    engine: 'exe',
    cmd: cfgExePath || join(here, 'bin', 'lens.exe'),
    pre: [],
    env: undefined,
  }
}

export function apply(ctx, config) {
  const cfg = config && typeof config === 'object' ? config : {}
  const settings = readSettings(cfg)
  const plan = enginePlan(process.platform, HERE, cfg.exePath)
  const workDir = cfg.workDir || join(tmpdir(), 'lens-computer-control')
  const timeoutMs = settings.timeoutMs
  const hudPort = Number(unwrap(cfg.hudPort, 45871)) > 0 ? Number(unwrap(cfg.hudPort, 45871)) : 45871

  /** HUD 状态：动作时间线 + 急停闸。急停一旦拉起，所有 computer_* 工具立刻拒绝执行。 */
  const hud = {
    stopped: false,
    startedAt: Date.now(),
    timeline: [],
    lastScan: null,
    permission: null,
  }

  /** 权限类失败（macOS 的 TCC 授权没给）会落到这里，卡片据此显示引导而不是"离线"。 */
  const notePermission = (result) => {
    if (!result || typeof result !== 'object') return
    const code = String(result.code || '')
    const msg = String(result.message || result.error || '')
    if (
      /PERMISSION|not authorized|not permitted|TCC|辅助功能|screen recording|屏幕录制|JXA|osascript|not allowed|-1700|EACCES|ENOENT|ENGINE_TIMEOUT/i.test(
        code + ' ' + msg,
      )
    ) {
      hud.permission = { at: Date.now(), code, message: msg.slice(0, 200) }
    } else if (result.ok === true) {
      hud.permission = null
    }
  }

  const record = (kind, detail, ok) => {
    hud.timeline.unshift({ at: Date.now(), kind, detail, ok })
    if (hud.timeline.length > 40) hud.timeline.length = 40
    pulseHud(kind, detail, ok)
  }

  /** 坐标点击的「预览 → 确认」：令牌 -> 那一次点击的参数。一次性、短时效。 */
  const pendingClicks = new Map()
  const PENDING_TTL = 180000

  try {
    mkdirSync(workDir, { recursive: true })
  } catch {
    /* workDir 可能已存在或无权限，运行期再报错 */
  }

  const runExe = (exe, args, env) =>
    new Promise((resolve) => {
      execFile(
        exe,
        args,
        { timeout: timeoutMs, windowsHide: true, maxBuffer: 64 * 1024 * 1024, env: env || process.env },
        (err, stdout, stderr) => {
          // P0-11：超时/被杀掉的命令要明确说是超时，而不是丢一句 "Command failed: ..."
          if (err && (err.killed || err.signal === 'SIGTERM' || err.code === 'ETIMEDOUT')) {
            resolve({
              ok: false,
              code: 'ENGINE_TIMEOUT',
              message: `引擎超时（${timeoutMs}ms）：${String(args[0] || '')} 没在时限内返回`,
              hint: '窗口越大 AX 遍历越慢；把 设置 → Lens 电脑控制 → 单次调用超时 调大，或改用 computer_vision 切块。',
              args: args.slice(0, 6).join(' '),
            })
            return
          }
          const out = String(stdout || '').trim()
          if (out) {
            const parsed = parseLoose(out)
            if (parsed !== null) {
              resolve(toJsonSafe(parsed))
              return
            }
            resolve({
              ok: false,
              code: 'BAD_OUTPUT',
              message: '引擎输出无法解析为 JSON',
              raw: out.slice(0, 4000),
              stderr: String(stderr || '').slice(0, 1000),
              exitCode: err && typeof err.code === 'number' ? err.code : null,
            })
            return
          }
          resolve({
            ok: false,
            error: err ? String(err.message || err) : 'no output',
            stderr: String(stderr || '').slice(0, 2000),
          })
        },
      )
    })

  /** 所有引擎调用都从这里走：Windows 直接调用 exe，macOS 用 node 跑 engine-mac.mjs。 */
  const runRaw = (args) => runExe(plan.cmd, plan.pre.concat(args), plan.env)

  // macOS 的控制权限（辅助功能 / 屏幕录制）需要"申请"才会有弹窗：
  // 光靠用户自己去设置里翻，十有八九会漏。这里在**第一次真正调用引擎之前**
  // 主动申请一次，系统弹窗会把宿主加进对应列表，用户只要勾选。
  let permissionState = null
  let permissionAsked = false
  const ensurePermissionsOnce = async () => {
    if (plan.engine === 'exe' || permissionAsked) return
    permissionAsked = true
    try {
      permissionState = await runRaw(['perm', '--action', 'request'])
      if (permissionState && permissionState.granted === false) {
        hud.permission = {
          at: Date.now(),
          code: 'PERMISSION',
          message: permissionState.hint || '缺少系统授权',
        }
      } else if (permissionState && permissionState.granted === true) {
        hud.permission = null
      }
    } catch {
      /* 权限申请失败不影响后续（工具调用会再报具体错误） */
    }
  }

  // 首次调用时把视觉反馈层拉起来（渐变蓝边 + 顶部胶囊）。失败不影响主流程。
  let hudReady = false
  let hudNative = plan.engine === 'exe'
  const ensureHudOnce = async () => {
    if (hudReady) return
    hudReady = true
    // 设置里关掉 HUD 就彻底不启动那个窗口：省一次进程，也让"完全静默"成为可选
    if (!settings.hudEnabled) return
    try {
      const r = await runRaw(['hud', '--action', 'start'])
      // macOS 的浮层是运行时用 swiftc 编出来的，编不出来就退回"只有 Web 状态卡"
      if (plan.engine !== 'exe') {
        hudNative = !!(r && r.ok === true && r.running !== false)
        if (!hudNative) {
          record('hud', '浮层不可用：' + (r && (r.message || r.code)) || 'swiftc 缺失', false)
        }
      }
    } catch {
      /* HUD 只是锦上添花 */
    }
  }

  /** 每次动作后在屏幕边缘闪一下（macOS 的浮层靠这个文件驱动）。 */
  const pulseHud = (kind, detail, ok) => {
    if (!hudNative) return
    const text = String(detail || kind || '').slice(0, 60)
    runRaw(['hud', '--action', 'pulse', '--text', (ok ? '' : '✗ ') + text, '--kind', String(kind || '')]).catch(
      () => {},
    )
  }

  /** 设置里禁用人类化节奏后，给动作类命令补 --instant（点击/输入/按键/拖动）。 */
  const HUMANIZED_VERBS = new Set(['click', 'type', 'key', 'drag', 'paste'])
  const withSettings = (args) => {
    if (settings.humanize || !HUMANIZED_VERBS.has(args[0])) return args
    return args.includes('--instant') ? args : args.concat(['--instant'])
  }

  /** 命中「永不操作的窗口」列表时返回命中的关键词。 */
  const blockedHit = (windowKey) => {
    if (!settings.blockedTitles.length) return null
    const hay = String(windowKey || '').toLowerCase()
    if (!hay) return null
    for (const word of settings.blockedTitles) {
      if (hay.includes(String(word).toLowerCase())) return word
    }
    // 用候选 id 点击时没有窗口参数，退回上次扫描记下的窗口标题
    const scanned = hud.lastScan && hud.lastScan.window
    if (!windowKey && scanned) {
      const title = String(scanned).toLowerCase()
      for (const word of settings.blockedTitles) {
        if (title.includes(String(word).toLowerCase())) return word
      }
    }
    return null
  }

  /** 每次引擎调用都进时间线，HUD 靠它显示"它刚才干了什么"。 */
  const run = async (args) => {
    await ensurePermissionsOnce()
    await ensureHudOnce()
    const verb = args[0]
    if (hud.stopped && verb !== 'info' && verb !== 'verify' && verb !== 'focus') {
      record(verb, 'blocked by 急停', false)
      return { ok: false, error: 'EMERGENCY_STOP: 急停已生效，动作被拒绝。请让用户先解除急停。' }
    }
    // 黑名单：宁可拒绝也不要在你不希望被碰的窗口上动手
    const winArg = args.indexOf('--window') >= 0 ? args[args.indexOf('--window') + 1] : null
    const blocked = blockedHit(winArg)
    if (blocked) {
      record(verb, `blocked by 黑名单 ${blocked}`, false)
      return {
        ok: false,
        code: 'WINDOW_BLOCKED',
        error: `窗口「${winArg || (hud.lastScan && hud.lastScan.window) || ''}」命中设置里的屏蔽词「${blocked}」，动作被拒绝。`,
        hint: '要去掉这条限制，在 设置 → 插件 → Lens 里删掉这个关键词。',
      }
    }
    const label = args
      .slice(1)
      .map((a) => String(a))
      .join(' ')
    // 把宿主 pid 传给引擎（追加在末尾：第一个参数必须是命令）：它的窗口绝不能成为操作目标
    const result = await runRaw(withSettings(args).concat(['--excludePid', String(process.pid)]))
    const ok = result && result.ok !== false
    notePermission(result)
    record(verb, label.slice(0, 90), ok)
    if (verb === 'scan' && result && result.ok) {
      const cands = result.candidates || []
      const byChannel = {}
      for (const c of cands) byChannel[c.channel || '?'] = (byChannel[c.channel || '?'] || 0) + 1
      hud.lastScan = {
        at: Date.now(),
        window: result.window && result.window.title,
        level: result.capability && result.capability.level,
        candidates: cands.length,
        byChannel,
        fingerprint: result.fingerprint,
        overlay: result.overlay,
      }
    }
    return result
  }

  // ---- 坐标点击的「预览 → 确认」：工具与 HUD 面板共用这一份实现 ----
  /** 预览列表（给 HUD 面板渲染待确认卡片用）。 */
  const pendingList = () =>
    [...pendingClicks.entries()]
      .map(([token, p]) => ({
        token,
        x: p.x,
        y: p.y,
        window: p.window,
        at: p.at,
        ageMs: Date.now() - p.at,
        inside: p.inside,
        covered: !!p.covered,
        candidate: p.candidate || null,
      }))
      .sort((a, b) => b.at - a.at)
      .slice(0, 5)

  /** 真正落点。点的是令牌里记住的坐标，不是调用方这次传进来的 —— 防止「看的是 A、点的是 B」。 */
  /**
   * 确认一次预览。actor='model' 是工具调用方，actor='human' 是 HUD 上的按钮。
   * 设置里开了「点击必须由人工确认」后，模型这条路径直接拒绝——令牌保留，等人来点。
   */
  const confirmPending = async (token, actor) => {
    const pending = pendingClicks.get(token)
    if (!pending) {
      return {
        ok: false,
        code: 'CONFIRM_INVALID',
        message: '确认令牌无效或已经用过（预览只能确认一次）',
        hint: '重新调一次 computer_act 只给坐标，会重新出图并给新令牌。',
      }
    }
    if (settings.humanConfirm && actor !== 'human') {
      return {
        ok: false,
        code: 'HUMAN_CONFIRM_REQUIRED',
        message: '设置里开启了「点击必须由人工确认」，模型不能自行确认这次点击。',
        hint: '请在 HUD 状态卡（右下角）点「确认点击」，或在 设置 → Lens 电脑控制 里关掉这一项。',
        pending: { x: pending.x, y: pending.y, window: pending.window, at: pending.at },
      }
    }
    pendingClicks.delete(token)
    if (Date.now() - pending.at > PENDING_TTL) {
      return {
        ok: false,
        code: 'CONFIRM_EXPIRED',
        message: '预览已过期（超过 ' + Math.round(PENDING_TTL / 1000) + ' 秒），为安全起见请重新预览。',
        hint: '再调一次 computer_act 只给坐标即可。',
      }
    }
    const argv0 = ['click', '--window', pending.window, '--x', String(pending.x), '--y', String(pending.y)]
    if (pending.button) argv0.push('--button', String(pending.button))
    if (pending.count) argv0.push('--count', String(pending.count))
    if (pending.background) argv0.push('--bg')
    record('click', `confirmed (${pending.x},${pending.y})`, true)
    const done = await run(argv0)
    return Object.assign({}, done, {
      confirmed: true,
      clickedAt: { x: pending.x, y: pending.y },
      preview: pending.image,
      note: '这次是确认后的真实点击。',
    })
  }

  /** 取消一次待确认的点击（HUD 上的「取消」按钮走这里）。 */
  const cancelPending = (token) => {
    const had = pendingClicks.delete(token)
    if (had) record('cancel', ` discarded ${token}`, true)
    return { ok: true, cancelled: had }
  }

  // ---------------------------------------------------------------- tool 1
  const windowsTool = {
    name: 'computer_windows',
    description:
      '列出可见顶层窗口（标题/hwnd/DPI/进程/最小化/可见边框）。定位目标窗口的第一步：之后用标题片段或 hwnd，不要靠猜坐标。',
    parameters: {
      type: 'object',
      properties: {
        filter: { type: 'string', description: '可选，只返回标题或进程名包含该子串的窗口' },
        probe: {
          type: 'string',
          description:
            '试抓一帧并给每条窗口补 capturable / captureMethod / blackRatio。传关键词只探测匹配的窗口（推荐，每次探测都真抓一帧），传 "*" 探测全部。用来回答"是看不见，还是根本抓不到"。',
        },
      },
      additionalProperties: false,
    },
    output: {
      schema: {
        type: 'object',
        properties: {
          ok: { type: 'boolean' },
          count: { type: 'number' },
          windows: { type: 'array', items: { type: 'object' } },
          error: { type: 'string' },
        },
        additionalProperties: true,
      },
      render: (_args, value) => {
        if (!value || value.ok !== true) return text(`列出窗口失败：${(value && value.error) || '未知错误'}`)
        const lines = value.windows.map(
          (w) =>
            `hwnd=${w.hwnd} dpi=${w.dpi} ${w.minimized ? '[最小化]' : ''} ${w.process} | ${w.title}  (${w.frame.w}x${w.frame.h} @ ${w.frame.x},${w.frame.y})`,
        )
        return text(`共 ${value.count} 个窗口：\n${lines.join('\n')}`)
      },
    },
    async execute(args) {
      const probeKey = args?.probe === undefined ? null : String(args.probe)
      const infoArgs = ['info', '--json']
      if (probeKey !== null) infoArgs.push('--probe', probeKey === 'true' || probeKey === 'all' ? '*' : probeKey)
      const list = (await run(infoArgs)) || []
      if (!Array.isArray(list)) {
        return { ok: false, error: 'info 命令未返回窗口数组', detail: list }
      }
      const filter = String(args?.filter || '').trim().toLowerCase()
      const filtered = filter
        ? list.filter(
            (w) =>
              String(w.title || '').toLowerCase().includes(filter) ||
              String(w.process || '').toLowerCase().includes(filter),
          )
        : list
      return { ok: true, count: filtered.length, windows: filtered }
    },
  }

  // ---------------------------------------------------------------- tool 2
  const scanTool = {
    name: 'computer_scan',
    description:
      '扫描一个窗口 → 候选表 + 带编号的标注图。**有文字/标准控件的界面首选它**，一次拿全候选。\n· 候选含 id、通道、类型、中心点 screen{cx,cy}、文本。**只用 id 指定点击，绝不自己编坐标。**\n· 候选里没有目标、或返回出现「扫描质量差」→ **立刻转 computer_vision，不要重扫**。\n· 通道：uia / solid(实心按钮) / ocr(文字) / table(表格) / template(锚点)。微信、QQ、券商行情、Java 客户端 UIA 常为空，靠后四个兜住。\n· overlay 是标注图路径，需要看图就 read_image。自绘窗口 OCR 会认错字（取消→取肖），find=text 用短关键词。',
    parameters: {
      type: 'object',
      properties: {
        window: { type: 'string', description: '窗口标题片段或 hwnd' },
        channels: {
          type: 'string',
          description: '可选，限定通道，逗号分隔：uia,solid,ocr,table,template。默认全开。',
        },
        noRestore: {
          type: 'boolean',
          description: '可选，窗口最小化时不要自动恢复（只看不碰）',
        },
      },
      required: ['window'],
      additionalProperties: false,
    },
    output: {
      schema: {
        type: 'object',
        properties: {
          ok: { type: 'boolean' },
          window: { type: 'object' },
          capability: { type: 'object' },
          overlay: { type: 'string' },
          candidates: { type: 'array', items: { type: 'object' } },
          notes: { type: 'array', items: { type: 'string' } },
          error: { type: 'string' },
        },
        additionalProperties: true,
      },
      render: (_args, value) => {
        if (!value) return text('扫描无返回')
        if (value.ok !== true) {
          const why = [value.code || value.error || 'UNKNOWN', value.message || ''].join(' ').trim()
          return text(`扫描失败 [${why}]` + (value.hint ? `\n建议：${value.hint}` : '') + (value.notes ? '\n' + value.notes.join('\n') : ''))
        }
        const cap = value.capability || {}
        const head = [
          `窗口: ${value.window?.title} (hwnd=${value.window?.hwnd}, dpi=${value.window?.dpi}, ${value.window?.frame?.w}x${value.window?.frame?.h} @ ${value.window?.frame?.x},${value.window?.frame?.y})`,
          `能力: level=${cap.level} 捕获=${cap.captureMethod} 非黑=${cap.nonBlackRatio} uia=${cap.uiaUsable ? '可用' : '不可用'}/${cap.uiaNodes}节点 ocr=${cap.ocrWords}词 表格=${cap.tableDetected ? '有' : '无'}`,
          `候选: ${value.candidates?.length || 0} 个，标注图: ${value.overlay || '无'}`,
          value.fingerprint
            ? `指纹: ${value.fingerprint}  ← 逐格/按坐标操作时可回传给 computer_act {expectedFingerprint:"${value.fingerprint}"}，界面一变就会被拒绝`
            : null,
        ].filter(Boolean)
        const rows = (value.candidates || []).slice(0, 80).map((c) => {
          const t = c.text ? ` "${c.text}"` : ''
          const col = c.color ? ` ${c.color}` : ''
          return `  #${c.id} ${c.channel}/${c.kind}${t}${col} screen=(${c.screen.cx},${c.screen.cy}) ${c.screen.w}x${c.screen.h} score=${c.score}`
        })
        const more =
          (value.candidates?.length || 0) > 80 ? `\n  …还有 ${value.candidates.length - 80} 个候选` : ''

        // 动态触发：扫描质量差的时候，直接在结果里点名让模型换 vision。
        // 只写在工具描述里没用——模型看的是这次返回的内容。
        const n = value.candidates?.length || 0
        const weak = []
        if (!cap.uiaUsable && n < 8) weak.push(`UIA 不可用且只有 ${n} 个候选`)
        if (n === 0) weak.push('一个候选都没有')
        // OCR 词数极少通常意味着"内容区完全没认出来"，UIA 这时往往只给了标题栏控件
        if (cap.ocrWords !== undefined && cap.ocrWords < 10 && n < 20) weak.push(`OCR 只认出 ${cap.ocrWords} 个词`)
        if (!cap.uiaUsable && !cap.ocrWords) weak.push('识别通道基本失灵')
        const advice = weak.length
          ? `\n\n⚠ 本次扫描质量差（${weak.join('；')}）。如果候选里没有你要点的东西，**别再重复扫描或猜坐标**，` +            `直接用 computer_vision 从画面里逐格定位——它不需要任何识别通道，一定能找到。`
          : ''
        return text(`${head.join('\n')}\n${rows.join('\n')}${more}${advice}\n通道日志: ${(value.notes || []).join(' | ')}`)
      },
    },
    async execute(args) {
      const w = String(args?.window || '').trim()
      if (!w) return { ok: false, error: '缺少 window 参数' }
      const stamp = Date.now().toString(36)
      const overlay = `${workDir.replace(/\\/g, '/')}/overlay-${stamp}.png`
      const argv = ['scan', '--window', w, '--json', '--overlay', overlay]
      if (args?.channels) argv.push('--channels', String(args.channels))
      if (args?.noRestore) argv.push('--no-restore')
      const res = await run(argv)
      if (res && res.ok !== undefined) return res
      return res || { ok: false, error: 'scan 无输出' }
    },
  }

  // ---------------------------------------------------------------- tool 3
  const actTool = {
    name: 'computer_act',
    description:
      '对窗口执行动作。定位优先级：id（引擎算坐标）> cell（网格）> x/y（自己算，必须预览确认）。\n· 键鼠：combo="ctrl+a"；text 支持中文；**超过 20 字用 action:"paste"**（原子操作，自动还原剪贴板）。\n· **坐标点击默认只预览、不落点**：给 x/y 会先回一张绿框+放大镜的图和一个 ck_ 令牌；确认位置无误后调 {window, confirm:"ck_xxx"} 才真点。令牌一次性、180 秒有效；id/cell 点击不受限。\n· force:true 跳过预览直接点（谨慎）；background:true 用窗口消息点击，不抢焦点。\n· 返回 window_gone = 动作后窗口关闭，通常是点对了。\n· 点不准别反复试坐标：先 scan 拿 id；没有候选再用 vision。',
    parameters: {
      type: 'object',
      properties: {
        window: { type: 'string', description: '窗口标题片段或 hwnd' },
        action: {
          type: 'string',
          enum: ['click', 'key', 'type', 'paste', 'move', 'focus', 'scroll', 'wait'],
          description: '要执行的动作；省略时按 id/坐标推断为 click',
        },
        id: { type: 'number', description: 'computer_scan 返回的候选编号（click 用）' },
        cell: { type: 'string', description: '网格单元 "列,行"（computer_vision mode:"grid" 得到），凑不齐候选时用它精确定位' },
        x: { type: 'number', description: '屏幕物理坐标 x（无 id 时使用；scroll 时是滚动点）' },
        y: { type: 'number', description: '屏幕物理坐标 y（无 id 时使用；scroll 时是滚动点）' },
        button: { type: 'string', enum: ['left', 'right', 'middle'], description: '鼠标键，默认 left' },
        count: { type: 'number', description: '点击次数，默认 1；2 表示双击' },
        combo: { type: 'string', description: '组合键，如 ctrl+shift+s' },
        text: { type: 'string', description: '要输入的文字' },
        background: { type: 'boolean', description: '后台消息点击，不抢焦点' },
        delta: { type: 'number', description: 'scroll 时滚几格：正=上/左，负=下/右，默认 -3' },
        horizontal: { type: 'boolean', description: 'scroll 时横向滚动' },
        expectedFingerprint: { type: 'string', description: '扫描时拿到的窗口指纹；给了就校验界面有没有变过，变了就拒绝执行（防止按旧坐标打偏）' },
        confirm: {
          type: 'string',
          description:
            '预览令牌：坐标点击默认会先回一张绿框图（不落点）并给一个 ck_xxx 令牌；把它传回来才真的点。令牌一次性、180 秒有效',
        },
        force: {
          type: 'boolean',
          description: '跳过坐标预览直接点（谨慎使用；常规流程应该是先预览再 confirm）',
        },
        condition: { type: 'string', enum: ['exists', 'visible', 'foreground'], description: 'wait 时等什么条件' },
        timeout: { type: 'number', description: 'wait 时最长等待毫秒，默认 8000' },
        verify: {
          type: 'boolean',
          description: '动作后自动做像素差分核验（省一次 computer_verify 调用）；changed=false 说明没生效，应换策略而不是重复点',
        },
      },
      required: ['window'],
      additionalProperties: false,
    },
    output: {
      schema: {
        type: 'object',
        properties: {
          ok: { type: 'boolean' },
          x: { type: 'number' },
          y: { type: 'number' },
          button: { type: 'string' },
          notes: { type: 'array', items: { type: 'string' } },
          error: { type: 'string' },
        },
        additionalProperties: true,
      },
      render: (_args, value) => {
        if (!value) return text('动作无返回')
        if (value.ok === false) {
          const lines = [`动作失败 [${value.code || value.error || 'UNKNOWN'}] ${value.message || ''}`]
          if (value.target) lines.push(`目标窗口：${describe(value.target)}`)
          if (value.coveredBy) lines.push(`被遮挡于：${describe(value.coveredBy)}`)
          if (value.hint) lines.push(`建议：${value.hint}`)
          if (value.raw) lines.push(`原始输出：${String(value.raw).slice(0, 300)}`)
          if (value.notes?.length) lines.push(`细节：${value.notes.join('；')}`)
          lines.push('提示：若原因是「找不到目标 / 候选对不上」，不要反复重试——改用 computer_vision 从画面逐格定位。')
          return text(lines.join('\n'))
        }
        const lines = []
        const bits = []
        // wait 的结果是 {for, elapsedMs, window}，没有坐标，单独渲染
        if (value.for && value.elapsedMs !== undefined) {
          return text(`等待完成：${value.for} 已满足（${value.elapsedMs}ms）${value.window?.title ? ' — ' + value.window.title : ''}`)
        }
        // 坐标点击的预览：**没有点击**，回一张绿框图等确认
        if (value.code === 'CLICK_NEEDS_CONFIRM') {
          const out = [
            `⚠ 还没有点击 —— 这是坐标点击的预览（绿框 = 会落点的范围 ${value.box?.w}x${value.box?.h}）`,
            `标注图：${value.image}`,
            `坐标 (${value.point?.x}, ${value.point?.y})${value.pixel ? `  该点像素 ${value.pixel}` : ''}`,
          ]
          if (value.candidate) out.push(`命中候选：#${value.candidate.id} ${value.candidate.text || ''}`)
          if (value.covered) out.push(`⚠ 这一点此刻被别的窗口盖住：${value.coveredBy || '未知窗口'}`)
          if (value.inside === false) out.push('⚠ 这一点落在窗口外面')
          out.push(`确认后：computer_act { window: "<同一个窗口>", confirm: "${value.token}" }`)
          out.push('要换位置就重新给坐标再来一次预览；确认令牌一次性、180 秒内有效。')
          if (value.hint) out.push(`说明：${value.hint}`)
          return text(out.join('\n'))
        }
        if (value.combo) return text(`已发送按键 ${value.combo}${value.notes?.length ? '（' + value.notes.join('；') + '）' : ''}`)
        if (value.pasted) return text(`已粘贴 ${value.pasted} 个字符${value.notes?.length ? '（' + value.notes.join('；') + '）' : ''}`)
        if (value.chars !== undefined && value.x === undefined) {
          return text(`已输入 ${value.chars} 个字符${value.foreground === false ? '（注意：发送时目标窗口不在前台）' : ''}`)
        }
        if (value.x !== undefined) bits.push(`坐标 (${value.x},${value.y})`)
        if (value.button) bits.push(`按钮 ${value.button}`)
        if (value.warning === 'window_gone') {
          lines.push('动作已执行；目标窗口随后关闭（通常说明点对了，例如登录成功后登录窗消失）')
        }
        lines.push(`动作已执行 ${bits.length ? '— ' + bits.join('，') : ''}`)
        if (value.confirmed) lines.push(`（这是确认后的真实点击：${value.clickedAt?.x},${value.clickedAt?.y}；预览图 ${value.preview}）`)
        if (value.notes?.length) lines.push(`细节：${value.notes.join('；')}`)
        if (value.verify) {
          if (!value.verify.changed) {
            lines.push('核验：画面没变化（changed=false）——这一下可能没生效，换策略而不是重复点')
          } else if (value.verify.hoverSuspected) {
            // 变化只局限在一小块 → 典型是鼠标悬停高亮，几乎肯定不是"点成功"
            const box = value.verify.box ? `（变化区域约 ${value.verify.box.w}x${value.verify.box.h}）` : ''
            lines.push(`核验：只有一小块像素变了${box}，**大概率只是悬停高亮或光标闪烁，不是动作生效**。`)
            lines.push('换判据：computer_look 看内容，或 computer_scan 看有没有出现预期的候选。')
          } else {
            // 像素差分只能证明"画过东西"，证明不了"点对了"：鼠标悬停本身就会改像素。
            lines.push(`核验：画面有变化（changed=true，比例 ${value.verify.ratio ?? '?'}）—— 这只说明界面重绘过，**不等于点对了**。`)
            lines.push('要确认结果，用 computer_look 看内容，或 computer_scan 看有没有出现预期的候选。')
          }
        }
        return text(lines.join('\n'))
      },
    },
    async execute(args) {
      const w = String(args?.window || '').trim()
      if (!w) return { ok: false, code: 'BAD_ARGS', message: '缺少 window 参数' }

      // 黑名单先拦：连预览都不给，别把"看起来要往禁区点"的交互摆到台面上
      const blockedEarly = blockedHit(w)
      if (blockedEarly) {
        record('blocked', `黑名单 ${blockedEarly}`, false)
        return {
          ok: false,
          code: 'WINDOW_BLOCKED',
          error: `窗口「${w}」命中设置里的屏蔽词「${blockedEarly}」，动作被拒绝。`,
          hint: '要去掉这条限制，在 设置 → Lens 电脑控制 里删掉这个关键词。',
        }
      }

      // B13：action 必须白名单命中，未识别就明确报错。
      // 旧实现会静默落到 click 兜底，x/y 缺省成 0 → 在屏幕左上角点了一下还报 ok:true。
      const ALLOWED = ['click', 'key', 'type', 'paste', 'move', 'focus', 'scroll', 'wait']
      const action = String(args?.action || 'click')
      if (!ALLOWED.includes(action)) {
        return {
          ok: false,
          code: 'BAD_ACTION',
          message: `不支持的 action: ${action}`,
          allowed: ALLOWED,
          hint: '想等界面变化用 action:"wait"；想点击用 id / cell / x,y 之一。',
        }
      }

      if (action === 'wait') {
        const wv = ['wait', '--window', w, '--for', String(args?.condition || 'exists')]
        wv.push('--timeout', String(args?.timeout || 8000))
        return run(wv)
      }
      if (action === 'focus') return run(['focus', '--window', w])
      if (action === 'paste') {
        // 长文本用粘贴：一次原子操作，不怕中途焦点漂移；用完会把原剪贴板还原
        await run(['focus', '--window', w])
        const txt = String(args?.text || '')
        if (!txt) return { ok: false, code: 'BAD_ARGS', message: 'paste 需要 text' }
        return run(['paste', '--text', txt])
      }
      if (action === 'key' || action === 'type') {
        // 键盘事件发给前台窗口，所以先把目标窗口弄到前台
        await run(['focus', '--window', w])
        if (action === 'key') return run(['key', '--combo', String(args?.combo || '')])
        return run(['type', '--text', String(args?.text || '')])
      }
      if (action === 'move') {
        if (args?.x === undefined || args?.y === undefined)
          return { ok: false, code: 'BAD_ARGS', message: 'move 需要 x 和 y' }
        return run(['move', '--x', String(args.x), '--y', String(args.y)])
      }
      if (action === 'scroll') {
        const sv = ['scroll', '--instant']
        if (w) sv.push('--window', w)
        if (args?.x !== undefined) sv.push('--x', String(args.x))
        if (args?.y !== undefined) sv.push('--y', String(args.y))
        if (args?.delta !== undefined) sv.push('--delta', String(args.delta))
        if (args?.count) sv.push('--times', String(args.count))
        if (args?.horizontal) sv.push('--horizontal')
        return run(sv)
      }

      // 点击：id / cell / (x,y) 三者必须给其一，绝不再静默退回 (0,0)
      // confirm 是另一条合法入口（凭上次预览的令牌落点），不需要再给坐标
      if (!args?.confirm && args?.id === undefined && !args?.cell && (args?.x === undefined || args?.y === undefined)) {
        return {
          ok: false,
          code: 'BAD_ARGS',
          message: '点击需要 id / cell / x+y 三者之一（或用 confirm 令牌落点）',
          hint: '先 computer_scan 拿候选 id；或 computer_vision 定位后给 x,y —— 给坐标会先出预览图，确认后才点。',
        }
      }

      // 设置里开了「自动坐标检验」：自己算的坐标先验一遍，被遮挡/越界就直接拒绝，
      // 不让你的一次误判变成一次误点。id / cell 走的是引擎算出来的坐标，不受影响。
      if (settings.autoCheck && args?.id === undefined && !args?.cell) {
        const stamp = Date.now().toString(36)
        const chk = await runRaw([
          'check', '--window', w,
          '--x', String(Math.round(args.x)), '--y', String(Math.round(args.y)),
          '--w', String(settings.checkBox), '--h', String(settings.checkBox),
          '--zoom', String(settings.checkZoom),
          '--out', `${workDir.replace(/\\/g, '/')}/autocheck-${stamp}.png`,
        ])
        record('check', `auto (${args.x},${args.y})`, chk && chk.ok !== false)
        // 遮挡不一定致命：background 点击走窗口消息，不要求窗口在最前面；
        // 只有真的落在窗口外才硬拒。被遮挡时降级成 warning 继续走预览/点击。
        if (chk && chk.ok === true && (chk.covered || chk.inside === false)) {
          const softCovered = chk.covered && (args?.background === true || args?.force === true)
          if (softCovered && chk.inside !== false) {
            record('check', `covered but background/force (${args.x},${args.y})`, true)
          } else {
          return {
            ok: false,
            code: chk.inside === false ? 'POINT_OUTSIDE' : 'TARGET_COVERED',
            message: chk.inside === false ? '坐标落在窗口外，已按设置拒绝点击' : '目标点被其他窗口遮挡，已按设置拒绝点击',
            image: chk.image,
            point: chk.point,
            coveredBy: chk.coveredBy,
            hint: '看图修正坐标，或先 focus 目标窗口；也可以在 设置 → 插件 → Lens 关掉「自动坐标检验」。\n如果这个窗口本来就不该在前台（托盘、后台应用），改用 { background: true } 走窗口消息点击。',
          }
          }
        }
        // 遮挡类失败再问一句"这窗口到底能不能抓"：能抓就说明只是不在最前面，
        // background 点击大概率可行；抓不到才是真不可操作。别让调用方靠 try/error 猜。
        if (chk && chk.ok === true && chk.covered) {
          const probe = await runRaw(['probe', '--window', w])
          if (probe && probe.ok === true) {
            return Object.assign({}, {
              ok: false,
              code: 'TARGET_COVERED',
              message: '目标点被其他窗口遮挡，已按设置拒绝点击',
              image: chk.image,
              point: chk.point,
              coveredBy: chk.coveredBy,
              capturable: probe.capturable === true,
              captureMethod: probe.captureMethod,
              blackRatio: probe.blackRatio,
              hint:
                probe.capturable === true
                  ? '这个窗口抓得到（' + (probe.captureMethod || '?') + '），说明只是不在最前面：改用 { background: true } 走窗口消息点击，或先 computer_window { action: "focus" }。'
                  : '这个窗口抓不到画面（黑屏比例 ' + probe.blackRatio + '），多半开了反截屏保护：只能做引导，无法代点。',
            })
          }
        }
      }

      // ---------------------------------------------------------- 坐标点击：先预览，不落点
      // 默认行为（设置 confirmClicks=true）：模型给 x/y 时**先不点**，跑一次坐标检验，
      // 回一张把命中范围画成绿框的图 + 一次性确认令牌；模型/用户确认后再用
      // computer_act { confirm: "<token>" } 真正点击。id / cell 用的是引擎算出来的坐标，
      // 不需要预览（那本来就是引擎的答案，不是模型的猜测）。
      const isCoordClick = args?.id === undefined && !args?.cell

      if (args?.confirm) return confirmPending(String(args.confirm), 'model')

      if (isCoordClick && settings.confirmClicks && args?.force !== true) {
        const stamp = Date.now().toString(36)
        const image = `${workDir.replace(/\\/g, '/')}/preclick-${stamp}.png`
        const px = Math.round(args.x)
        const py = Math.round(args.y)
        const chk = await runRaw([
          'check', '--window', w,
          '--x', String(px), '--y', String(py),
          '--w', String(settings.checkBox), '--h', String(settings.checkBox),
          '--zoom', String(settings.checkZoom),
          '--out', image,
        ])
        record('check', `preview (${px},${py})`, chk && chk.ok !== false)
        if (!chk || chk.ok !== true) {
          return Object.assign({ ok: false, clicked: false, code: 'PREVIEW_FAILED' }, chk || {})
        }
        const token = 'ck_' + Math.random().toString(36).slice(2, 10)
        pendingClicks.set(token, {
          at: Date.now(), window: w, x: px, y: py,
          button: args?.button, count: args?.count, background: args?.background, image: chk.image,
          inside: chk.inside !== false, covered: !!chk.covered,
          candidate: chk.candidate || null, pixel: chk.pixel || null,
        })
        // 只留最近 20 条，避免长时间会话里堆垃圾
        if (pendingClicks.size > 20) {
          const oldest = [...pendingClicks.entries()].sort((a, b) => a[1].at - b[1].at)[0]
          if (oldest) pendingClicks.delete(oldest[0])
        }
        const problems = []
        if (chk.inside === false) problems.push('这个点落在窗口外面')
        if (chk.covered) problems.push('这一点此刻被别的窗口盖住')
        return {
          ok: true,
          clicked: false,
          code: 'CLICK_NEEDS_CONFIRM',
          image: chk.image,
          point: chk.point,
          box: chk.box,
          inside: chk.inside,
          covered: chk.covered,
          coveredBy: chk.coveredBy,
          pixel: chk.pixel,
          candidate: chk.candidate,
          token,
          hint:
            '还没有点击。看图确认绿框压住的是不是你要的位置' +
            (problems.length ? `（注意：${problems.join('、')}）` : '') +
            `；确认无误就调 computer_act { confirm: "${token}" }，要换位置就重新给坐标预览。`,
        }
      }

      const argv = ['click', '--window', w]
      if (args?.cell) argv.push('--cell', String(args.cell))
      else if (args?.id !== undefined) argv.push('--id', String(args.id))
      else argv.push('--x', String(args.x), '--y', String(args.y))
      if (args?.button) argv.push('--button', String(args.button))
      if (args?.count) argv.push('--count', String(args.count))
      if (args?.background) argv.push('--bg')
      if (args?.expectedFingerprint) argv.push('--expectFingerprint', String(args.expectedFingerprint))
      const clicked = await run(argv)
      // verify=true：点完立刻差分核验，省掉一次单独的工具调用
      if (args?.verify && clicked && clicked.ok !== false) {
        const v = await run(['verify', '--window', w])
        return Object.assign({}, clicked, { verify: v })
      }
      return clicked
    },
  }

  // 坐标定位：切格子（递归下钻）/ 轴刻度网格 / 放大某格，三种模式合成一个工具
  const visionTool = {
    name: 'computer_vision',
    description:
      '纯视觉定位，不依赖任何识别通道。scan 给不出候选、或目标没有文字（图标/画布/游戏/远程桌面）时用它。\n· mode="cells"（默认）：区域切 N 格、每格印屏幕绝对坐标，你报格子编号；再传 path:"7" 只切那一格，逐级收窄（如 "7.4"）。\n· mode="grid"：窗口轴刻度网格；mode="zoom"：放大某格并叠二级网格（需 c/r）。\n· cells=9 每级形状稳定，推荐；已知大概位置用 rect:"x,y,w,h" 跳过下钻；mark:"x,y" 画准星核对。\n· 够准后 computer_act {x,y} —— 会先出预览图，确认后再点。',
    parameters: {
      type: 'object',
      properties: {
        mode: {
          type: 'string',
          enum: ['cells', 'grid', 'zoom'],
          description: 'cells=切格子（默认）；grid=窗口轴刻度网格；zoom=放大某格（需 c/r）',
        },
        path: { type: 'string', description: '下钻路径，如 "7" 选第 7 格，"7.4" 再选其中第 4 格；留空从全屏开始' },
        rect: { type: 'string', description: '直接指定区域 "x,y,w,h"（屏幕物理坐标），跳过逐级下钻' },
        mark: { type: 'string', description: '在 "x,y" 处画红色十字准星，用于确认目标位置' },
        cells: { type: 'number', description: '每级切几格，默认 10（推荐 9）' },
        cols: { type: 'number', description: '强制列数（与 rows 一起用）' },
        rows: { type: 'number', description: '强制行数（与 cols 一起用）' },
        c: { type: 'number', description: 'zoom 模式的列号' },
        r: { type: 'number', description: 'zoom 模式的行号' },
        sub: { type: 'number', description: 'zoom 的二级网格密度，默认 8' },
        window: { type: 'string', description: '只在某个窗口范围内操作；留空用整个桌面（grid/zoom 必填）' },
        scale: { type: 'number', description: '放大倍数，默认按区域大小自动（长边约 1500px）' },
      },
      additionalProperties: false,
    },
    output: {
      schema: {
        type: 'object',
        properties: {
          ok: { type: 'boolean' },
          path: { type: 'string' },
          level: { type: 'number' },
          image: { type: 'string' },
          cols: { type: 'number' },
          rows: { type: 'number' },
          cells: { type: 'array', items: { type: 'object' } },
          hint: { type: 'string' },
          code: { type: 'string' },
          message: { type: 'string' },
        },
        additionalProperties: true,
      },
      render: (_args, value) => {
        if (!value || value.ok !== true) {
          const why = [value?.code || 'FAILED', value?.message || ''].join(' ').trim()
          return text(`视觉定位失败 [${why}]${value?.hint ? `\n建议：${value.hint}` : ''}`)
        }
        if (value.cells && value.sub) {
          return text(`二级网格 ${value.sub}x${value.sub}（放大 ${value.scale}x）\n标注图：${value.image}\n读二级刻度后 computer_act {cell:"列,行"}，或取 cells 里的 cx,cy。`)
        }
        if (value.screenCellW) {
          return text(
            `轴刻度网格 ${value.cols}列 x ${value.rows}行，单格约 ${value.screenCellW}x${value.screenCellH} 屏幕像素\n` +
              `标注图：${value.image}\n报 (列,行) 后 computer_act {cell:"列,行"}；需要更准先 mode:"zoom"。`,
          )
        }
        const lines = [
          `第 ${value.level} 级：${value.cols}列 × ${value.rows}行，共 ${value.cells?.length || 0} 格，单格约 ${value.cellW}x${value.cellH} 屏幕像素`,
          `标注图：${value.image}`,
          `用 read_image 打开，每格左上角写着 #编号 和 x,y WxH @中心点（都是屏幕绝对坐标）。`,
        ]
        if (!value.path) lines.push('选好格子后：继续放大 → path:"编号"；够准了 → computer_act {x: cx, y: cy}。')
        else lines.push(`当前路径 ${value.path}；更准就传 path:"${value.path}.<编号>"，够准了就 computer_act {x,y}。`)
        return text(lines.join('\n'))
      },
    },
    async execute(args) {
      const stamp = Date.now().toString(36)
      const out = `${workDir.replace(/\\/g, '/')}/vision-${stamp}.png`
      const w = String(args?.window || '').trim()
      const mode = String(args?.mode || 'cells')

      if (mode === 'grid' || mode === 'zoom') {
        if (!w) return { ok: false, code: 'BAD_ARGS', message: 'grid/zoom 需要 window 参数' }
        if (mode === 'zoom') {
          if (args?.c === undefined || args?.r === undefined)
            return { ok: false, code: 'BAD_ARGS', message: 'zoom 需要 c 和 r（列号、行号）' }
          const zv = ['zoom', '--c', String(args.c), '--r', String(args.r), '--out', out]
          if (args?.sub) zv.push('--sub', String(args.sub))
          return run(zv)
        }
        const gv = ['grid', '--window', w, '--out', out]
        if (args?.cols) gv.push('--cols', String(args.cols))
        if (args?.rows) gv.push('--rows', String(args.rows))
        return run(gv)
      }

      const argv = ['vision', '--out', out]
      if (w) argv.push('--window', w)
      else argv.push('--desktop')
      if (args?.path) argv.push('--path', String(args.path))
      if (args?.rect) argv.push('--rect', String(args.rect))
      if (args?.mark) argv.push('--mark', String(args.mark))
      if (args?.cells) argv.push('--cells', String(args.cells))
      if (args?.cols && args?.rows) argv.push('--cols', String(args.cols), '--rows', String(args.rows))
      if (args?.scale) argv.push('--scale', String(args.scale))
      const v = await run(argv)
      if (!v || v.ok !== true) return v
      // 纯视觉流程也要能用 expectedFingerprint：带上最近一次 scan 的指纹。
      // 视觉定位之后窗口一挪，这些坐标就作废了，指纹是唯一能自动发现这件事的东西。
      const fp = hud.lastScan && (!w || String(hud.lastScan.window || '').includes(w))
        ? hud.lastScan.fingerprint
        : null
      return Object.assign({}, v, {
        fingerprint: fp || undefined,
        staleHint: fp
          ? '把 fingerprint 传给 computer_act / computer_check 的 expectedFingerprint：界面一变就会被拒绝执行。'
          : '这一轮还没有 scan 过该窗口，拿不到指纹；建议先 computer_scan 一次再走视觉流程。',
        previewNote: 'read_image 可能把图缩放显示（长边压到 ~1500-1700px）。坐标一律用格子里印的数字，别用目测像素反推屏幕坐标。',
      })
    },
  }

  // ---------------------------------------------------------------- tool 3.5
  const checkTool = {
    name: 'computer_check',
    description:
      '坐标检验（只读，不动鼠标、不发按键）。给坐标 → 回一张命中范围绿框（默认 24×24）+ 右下角放大镜的图，并说明：是否在窗口内、是否被遮挡、像素色、命中哪条候选。\n· 用在自己算的或视觉得到的坐标落点之前；点了没反应时也用它复核。\n· covered=true = 被其他窗口盖住，直接点会打错窗口；inside=false = 点在窗口外；candidate 有值可直接改用 id。\n· 绿框偏了就按偏差修正坐标再 check 一次。',
    parameters: {
      type: 'object',
      properties: {
        x: { type: 'number', description: '屏幕物理坐标 X（就是要检查的那个点击点）' },
        y: { type: 'number', description: '屏幕物理坐标 Y' },
        w: { type: 'number', description: '点击范围宽，默认 24（鼠标热区大小）' },
        h: { type: 'number', description: '点击范围高，默认同 w' },
        window: { type: 'string', description: '在哪个窗口范围内检查（标题片段或 hwnd）；留空=整屏桌面' },
        desktop: { type: 'boolean', description: '强制按整屏桌面检查' },
        zoom: { type: 'number', description: '放大镜倍率，默认 8（2~16）' },
      },
      required: ['x', 'y'],
      additionalProperties: false,
    },
    output: {
      schema: {
        type: 'object',
        properties: {
          ok: { type: 'boolean' },
          image: { type: 'string' },
          point: { type: 'object' },
          box: { type: 'object' },
          inside: { type: 'boolean' },
          covered: { type: 'boolean' },
          coveredBy: { type: 'string' },
          pixel: { type: 'string' },
          zoom: { type: 'number' },
          candidate: { type: 'object' },
          hint: { type: 'string' },
          code: { type: 'string' },
          message: { type: 'string' },
        },
        additionalProperties: true,
      },
      render: (_args, value) => {
        if (!value || value.ok !== true) {
          const why = [value?.code || 'FAILED', value?.message || value?.error || ''].join(' ').trim()
          return text(`坐标检验失败 [${why}]${value?.hint ? `\n建议：${value.hint}` : ''}`)
        }
        const lines = [
          `坐标 (${value.point.x}, ${value.point.y}) 的点击范围 ${value.box.w}x${value.box.h} → 屏幕矩形 (${value.box.x1},${value.box.y1})-(${value.box.x2},${value.box.y2})`,
          `标注图：${value.image}`,
          '用 read_image 打开：绿框=命中范围，右下角放大镜是同一处放大后的像素细节。',
        ]
        if (value.candidate) lines.push(`命中候选：#${value.candidate.id} ${value.candidate.text || '(无文字)'}  → 想稳就 computer_act {id: ${value.candidate.id}}`)
        if (value.pixel) lines.push(`该点像素色：${value.pixel}`)
        if (value.covered) lines.push(`⚠ 被遮挡：${value.coveredBy || '其他窗口'}（先 focus 目标窗口再点）`)
        if (value.inside === false) lines.push('⚠ 这个坐标不在目标窗口内。')
        if (value.hint) lines.push(`建议：${value.hint}`)
        return text(lines.join('\n'))
      },
    },
    async execute(args) {
      const stamp = Date.now().toString(36)
      const out = `${workDir.replace(/\\/g, '/')}/check-${stamp}.png`
      if (args?.x === undefined || args?.y === undefined)
        return { ok: false, code: 'BAD_ARGS', message: '需要 x 和 y（屏幕物理坐标）' }
      const argv = ['check', '--x', String(Math.round(args.x)), '--y', String(Math.round(args.y)), '--out', out]
      // 尺寸与倍率的默认值来自设置页（checkBox / checkZoom），单次调用可以覆盖
      argv.push('--w', String(Math.round(args?.w || settings.checkBox)))
      argv.push('--h', String(Math.round(args?.h || args?.w || settings.checkBox)))
      argv.push('--zoom', String(Math.round(args?.zoom || settings.checkZoom)))
      const w = String(args?.window || '').trim()
      if (args?.desktop || !w) argv.push('--desktop')
      else argv.push('--window', w)
      if (hud.stopped) return { ok: false, error: 'EMERGENCY_STOP: 急停已生效，动作被拒绝。请让用户先解除急停。' }
      const result = await runRaw(argv)
      record('check', `${argv[2]},${argv[4]}`, result && result.ok !== false)
      return result
    },
  }

  // ---------------------------------------------------------------- tool 4
  const runTool = {
    name: 'computer_run',
    description:
      '一次调用执行多步（脚本自动化），省掉多次往返。每行「动词 键=值 …」：\n  window <标题> / focus / click id=6 | click find=text:确定 | click x=100 y=200 / type text=你好 / key combo=ctrl+a\n  scroll x=500 y=400 delta=-3 times=2 / drag from=w100,w100 to=w300,w400 duration=600 / wait for=changed timeout=5000 / verify expect=changed / sleep ms=500\n· 默认遇错即停并返回逐步 trace；continue=true 则失败也继续。\n· **开启坐标预览时，脚本里的 click x= y= 会被拒（SCRIPT_COORD_CLICK_BLOCKED）**：改用 find=text / id，或给本工具传 force:true。\n· 适合固定套路：打开→输入→回车→核验。微信发消息走搜索框最稳：window 微信 / focus / key combo=ctrl+f / type text:张三 / key combo=enter。',
    parameters: {
      type: 'object',
      properties: {
        script: { type: 'string', description: '多行脚本，每行一个动作' },
        steps: { type: 'array', items: { type: 'string' }, description: '也可以直接传动作字符串数组，会自动拼成脚本' },
        continueOnError: { type: 'boolean', description: '某步失败后是否继续，默认 false' },
      },
      additionalProperties: false,
    },
    output: {
      schema: {
        type: 'object',
        properties: {
          ok: { type: 'boolean' },
          steps: { type: 'number' },
          okSteps: { type: 'number' },
          failed: { type: 'number' },
          ms: { type: 'number' },
          trace: { type: 'array', items: { type: 'object' } },
          error: { type: 'string' },
        },
        additionalProperties: true,
      },
      render: (_args, value) => {
        if (!value) return text('脚本无返回')
        if (value.error) return text(`脚本执行失败：${value.error}`)
        const lines = [`脚本执行完成：${value.okSteps}/${value.steps} 步成功` + (value.failed ? `，${value.failed} 步失败` : '') + `（${value.ms}ms）`]
        for (const t of value.trace || []) {
          const mark = t.ok ? '✓' : '✗'
          lines.push(`  ${mark} ${t.verb}${t.detail ? ` — ${String(t.detail).slice(0, 60)}` : ''}`)
        }
        return text(lines.join('\n'))
      },
    },
    async execute(args) {
      let script = String(args?.script || '').trim()
      if (!script && Array.isArray(args?.steps)) script = args.steps.map((s) => String(s)).join('\n')
      if (!script) return { ok: false, error: '缺少 script 或 steps' }

      // 坐标点击预览不能从脚本里绕过去：开了 confirmClicks 就拒绝带裸坐标的 click 行。
      // 脚本是"一次编排、不可中途确认"的形态，正好是预览要防的那类误点。
      if (settings.confirmClicks && args?.force !== true) {
        const offending = script
          .split('\n')
          .map((line, i) => ({ line: line.trim(), n: i + 1 }))
          .filter((x) => /^click\b/i.test(x.line) && /\b[xy]\s*=/.test(x.line))
        if (offending.length) {
          return {
            ok: false,
            code: 'SCRIPT_COORD_CLICK_BLOCKED',
            message:
              '脚本里的坐标点击（第 ' + offending.map((o) => o.n).join('、') + ' 行）被拦下了：开了「坐标点击先预览」时，裸坐标必须先在 computer_act 里预览确认。',
            offending: offending.map((o) => o.line),
            hint:
              '改用候选定位（click find=text:… 或 click id=…，坐标由引擎算，不受限制）；' +
              '确有必要在脚本里点坐标时，给 computer_run 传 force:true。',
          }
        }
      }

      const argv = ['run', '--text', script]
      if (args?.continueOnError) argv.push('--continue')
      return run(argv)
    },
  }

  const windowTool = {
    name: 'computer_window',
    description:
      '窗口管理：focus / maximize / minimize / restore / move / topmost / untopmost / wait / info。\n自动化前先把窗口摆到固定位置与大小，否则坐标和截图会随用户拖动漂移。move 可给 x/y/w/h（缺省沿用当前值）。\n**不提供 close**：关程序不可逆，需要关闭请让用户自己来。',
    parameters: {
      type: 'object',
      properties: {
        window: { type: 'string', description: '窗口标题片段或 hwnd' },
        action: {
          type: 'string',
          enum: ['focus', 'maximize', 'minimize', 'restore', 'move', 'topmost', 'untopmost', 'wait', 'info'],
          description: '要执行的操作，默认 info（只读）',
        },
        x: { type: 'number', description: 'move 时的屏幕 X' },
        y: { type: 'number', description: 'move 时的屏幕 Y' },
        w: { type: 'number', description: 'move 时的宽' },
        h: { type: 'number', description: 'move 时的高' },
        condition: { type: 'string', enum: ['exists', 'visible', 'foreground'], description: 'wait 时等什么条件，默认 exists' },
        timeout: { type: 'number', description: 'wait 时最长等待毫秒，默认 8000' },
      },
      required: ['window'],
      additionalProperties: false,
    },
    output: {
      schema: {
        type: 'object',
        properties: {
          ok: { type: 'boolean' },
          action: { type: 'string' },
          window: { type: 'object' },
          warning: { type: 'string' },
          code: { type: 'string' },
        },
        additionalProperties: true,
      },
      render: (_args, value) => {
        if (!value || value.ok !== true) {
          const lines = [`窗口操作失败 [${value?.code || 'FAILED'}] ${value?.message || ''}`]
          if (value?.hint) lines.push(`建议：${value.hint}`)
          return text(lines.join('\n'))
        }
        if (value.warning === 'window_gone') return text(`已${value.action || '执行'}；窗口随后关闭`)
        const f = value.window?.frame || {}
        return text(`${value.action || 'done'} 完成：${value.window?.title}  现在 ${f.w}x${f.h} @ ${f.x},${f.y}`)
      },
    },
    async execute(args) {
      const w = String(args?.window || '').trim()
      if (!w) return { ok: false, code: 'BAD_ARGS', message: '缺少 window 参数' }
      const act = String(args?.action || 'info')
      // 双保险：即使模型硬传 close 也拒绝——关闭应用不可逆，不该由 AI 触发
      if (act === 'close') {
        return {
          ok: false,
          code: 'CLOSE_NOT_ALLOWED',
          message: '已禁用 close 动作',
          hint: '需要关闭程序请让用户自己操作；你只负责操作界面，不负责结束进程。',
        }
      }
      // focus：从托盘唤出并置前（合并自原 computer_focus）
      if (act === 'focus') return run(['focus', '--window', w])
      // wait：等条件成立（合并自原 computer_wait）
      if (act === 'wait') {
        const wv = ['wait', '--window', w, '--for', String(args?.condition || 'exists')]
        wv.push('--timeout', String(args?.timeout || 8000))
        return run(wv)
      }
      if (plan.engine !== 'exe' && act === 'move' && args?.x === undefined && args?.y === undefined) {
        return {
          ok: false,
          code: 'BAD_ARGS',
          message: 'move 需要 x 或 y',
          hint: 'computer_window { action:"move", x:200, y:150, w:900, h:600 }',
        }
      }
      const argv = ['window', '--window', w, '--action', act]
      for (const k of ['x', 'y', 'w', 'h']) if (args?.[k] !== undefined) argv.push(`--${k}`, String(args[k]))
      const first = await run(argv)
      if (act !== 'move' || !first || first.ok === false) return first

      // move 的回报几何会带一层不可见的边框补偿（实测稳定：x +8、w −16、h −8），
      // 于是"照自己给的矩形算坐标"必然偏。这里不猜原因，直接按回报值迭代校准：
      // 每轮把偏差加到下一次请求上，最多 3 轮，落到 ±2px 内。
      const frameOf = (v) => (v && (v.frame || (v.window && v.window.frame))) || null
      const target = { x: args?.x, y: args?.y, w: args?.w, h: args?.h }
      const cur = {}
      for (const k of ['x', 'y', 'w', 'h']) if (target[k] !== undefined) cur[k] = Number(target[k])
      let actual = frameOf(first)
      let corrections = 0
      if (actual && Object.keys(cur).length) {
        for (let i = 0; i < 3; i++) {
          const dx = cur.x !== undefined ? target.x - actual.x : 0
          const dy = cur.y !== undefined ? target.y - actual.y : 0
          const dw = cur.w !== undefined ? target.w - actual.w : 0
          const dh = cur.h !== undefined ? target.h - actual.h : 0
          if (Math.abs(dx) <= 2 && Math.abs(dy) <= 2 && Math.abs(dw) <= 2 && Math.abs(dh) <= 2) break
          if (cur.x !== undefined) cur.x += dx
          if (cur.y !== undefined) cur.y += dy
          if (cur.w !== undefined) cur.w += dw
          if (cur.h !== undefined) cur.h += dh
          const fix = ['window', '--window', w, '--action', 'move']
          for (const k of ['x', 'y', 'w', 'h']) if (cur[k] !== undefined) fix.push(`--${k}`, String(Math.round(cur[k])))
          const again = await run(fix)
          corrections++
          const f = frameOf(again)
          if (!f) break
          actual = f
        }
      }
      return Object.assign({}, first, {
        frame: actual || first.frame,
        requested: target,
        corrections,
        geometryNote:
          'frame 是可见边框（不含 DWM 阴影）。引擎会对 move 做自动校准，所以 reportedframe ≈ 你请求的矩形；' +
          '要做坐标换算就用这里返回的 frame，不要用自己传进去的 x/y/w/h。',
      })
    },
  }

  const lookTool = {
    name: 'computer_look',
    description:
      '看一眼。mode="image"：截窗口/桌面拿干净原图（用 read_image 看布局）；mode="text"：OCR 读矩形区域。\n· 与 scan 的区别：scan 给候选表+标注图，look 给**没有标注的原始画面**，适合判断「界面上到底有什么」或读列表内容。\n· image 返回图片左上角屏幕坐标：图内 (px,py) → 屏幕 (x+px, y+py)；text 的 rect 相对窗口客户区。\n· text 可带 expect 做校验（已归一化全半角与易混字），别自己比字符串。',
    parameters: {
      type: 'object',
      properties: {
        window: { type: 'string', description: '窗口标题片段或 hwnd；image 模式留空则截整个桌面' },
        mode: { type: 'string', enum: ['image', 'text'], description: 'image=截图（默认），text=读文字' },
        rect: {
          type: 'string',
          description:
            '截取/读取的区域 "x,y,w,h"。image 与 text 都支持；坐标系由 space 决定（默认 auto：能当成屏幕坐标就当屏幕坐标）。',
        },
        space: {
          type: 'string',
          enum: ['auto', 'screen', 'client'],
          description:
            'rect 的坐标系：screen=屏幕绝对坐标，client=窗口客户区相对，auto=按数值自动判断（默认）。Windows 引擎内部只吃客户区坐标，会自动换算；macOS 引擎直接吃屏幕坐标，不做二次换算。返回里用 rectSpace 告诉你最终按哪套算的。',
        },
        expect: {
          type: 'string',
          description:
            'text 模式下给出期望文本 → 返回 match / similarity。比对已做归一化（全角半角、空格、易混字 0O/1Il/5S/8B），' +
            '所以 OCR 认错个别字也能判对。校验"我刚填进去的内容是否正确"时用它，别自己做字符串比较。',
        },
      },
      additionalProperties: false,
    },
    output: {
      schema: {
        type: 'object',
        properties: {
          ok: { type: 'boolean' },
          file: { type: 'string' },
          text: { type: 'string' },
          w: { type: 'number' },
          h: { type: 'number' },
          x: { type: 'number' },
          y: { type: 'number' },
        },
        additionalProperties: true,
      },
      render: (_args, value) => {
        if (!value || value.ok !== true) {
          return text(`查看失败 [${value?.code || 'FAILED'}] ${value?.message || value?.error || ''}`)
        }
        if (value.text !== undefined) {
          if (!value.text) return text('该区域没有识别到文字')
          const head = `读到文字（区域 ${value.rect?.w}x${value.rect?.h}）：\n${value.text}`
          if (value.match === undefined) return text(head)
          const verdict = value.match
            ? `✔ 与期望值一致（相似度 ${value.similarity}）`
            : `✘ 与期望值不一致（相似度 ${value.similarity}）—— 期望「${value.expect}」，实际「${value.text}」`
          return text(`${head}\n\n${verdict}`)
        }
        return text(
          `截图已保存：${value.file}\n` +
            `尺寸 ${value.w}x${value.h}，左上角在屏幕 (${value.x},${value.y})，方式=${value.method}\n` +
            `用 read_image 打开它；图内像素 (px,py) 对应屏幕坐标 (${value.x}+px, ${value.y}+py)。`,
        )
      },
    },
    async execute(args) {
      const w = String(args?.window || '').trim()
      const mode = String(args?.mode || 'image')
      const stamp = Date.now().toString(36)
      const out = `${workDir.replace(/\\/g, '/')}/shot-${stamp}.png`
      // ── text：rect 默认允许屏幕坐标，引擎只吃客户区坐标，这里统一换算
      if (mode === 'text') {
        if (!w) return { ok: false, code: 'BAD_ARGS', message: 'text 模式需要 window 参数' }
        let rect = null
        let rectSpace = 'client'
        if (args?.rect) {
          const nums = String(args.rect).split(',').map((v) => parseInt(v, 10))
          if (nums.length !== 4 || nums.some((n) => !Number.isFinite(n))) {
            return { ok: false, code: 'BAD_ARGS', message: 'rect 需要 "x,y,w,h"' }
          }
          rect = { x: nums[0], y: nums[1], w: nums[2], h: nums[3] }
          const want = String(args?.space || 'auto')
          let useScreen = want === 'screen'
          if (want === 'auto') {
            const info = await run(['info', '--json'])
            const list = Array.isArray(info) ? info : info?.windows || []
            const match = list.find((it) => String(it.title || '').includes(w) || String(it.process || '').includes(w))
            const client = match && (match.client || match.frame)
            if (client) {
              const looksScreen = rect.x >= client.x && rect.y >= client.y
              const fitsClient = rect.x + rect.w <= client.w && rect.y + rect.h <= client.h
              useScreen = looksScreen && !fitsClient
            }
          }
          if (useScreen && plan.engine === 'exe') {
            const info = await run(['info', '--json'])
            const list = Array.isArray(info) ? info : info?.windows || []
            const match = list.find((it) => String(it.title || '').includes(w) || String(it.process || '').includes(w))
            const client = match && (match.client || match.frame)
            if (client) {
              rect = { x: rect.x - client.x, y: rect.y - client.y, w: rect.w, h: rect.h }
              rectSpace = 'screen'
            }
          }
        }
        const argv = ['read', '--window', w, '--json']
        argv.push('--rect', rect ? `${rect.x},${rect.y},${rect.w},${rect.h}` : '0,0,99999,99999')
        if (args?.expect !== undefined) argv.push('--expect', String(args.expect))
        const res = await run(argv)
        if (res && typeof res === 'object') {
          res.rectSpace = rectSpace
          if (rectSpace === 'screen') res.rectScreen = { x: rect.x, y: rect.y, w: rect.w, h: rect.h }
        }
        return res
      }
      // ── image：rect 现在是真生效的（先给引擎，引擎不裁就在本地裁）
      const argv = ['cap', '--out', out]
      if (w) argv.push('--window', w)
      else argv.push('--desktop')
      if (args?.rect) argv.push('--rect', String(args.rect))
      const cap = await run(argv)
      if (!cap || cap.ok !== false) {
        if (cap && typeof cap === 'object') {
          cap.rectSpace = args?.rect ? 'screen' : 'window'
          // read_image 会缩放预览，明确告诉调用方别拿目测像素当屏幕坐标
          cap.previewNote = 'read_image 可能把图缩放显示；坐标请用返回的 x,y 加图内像素换算，别用肉眼估的比例。'
          if (args?.rect) cap.note = 'rect 已生效：这张图只包含该区域，图内 (0,0) 对应屏幕 (' + (cap.x ?? '?') + ',' + (cap.y ?? '?') + ')'
        }
      }
      return cap
    },
  }

  const tools = [windowsTool, scanTool, actTool, visionTool, checkTool, windowTool, lookTool, runTool]
  for (const tool of tools) {
    ctx.effect(() => ctx.tools.register(tool))
  }

  // ---- 急停闸：拉起后任何 computer_* 调用都被工具层直接拒绝 ----
  ctx.effect(() =>
    ctx.tools.guard((execution) => {
      if (!hud.stopped) return undefined
      if (!String(execution.name || '').startsWith('computer_')) return undefined
      return '急停已生效（Lens HUD 的红钮），请先点击解除急停再继续操作电脑。'
    }),
  )

  // ---- HUD 数据面：localhost 小服务（带 CORS，供 Web 面板轮询）----
  const cors = {
    'Access-Control-Allow-Origin': '*',
    'Access-Control-Allow-Methods': 'GET,POST,OPTIONS',
    'Access-Control-Allow-Headers': 'content-type',
    'Cache-Control': 'no-store',
  }

  const sendJson = (res, body) => {
    const text = JSON.stringify(body)
    res.writeHead(200, { ...cors, 'Content-Type': 'application/json; charset=utf-8' })
    res.end(text)
  }

  const newestOverlay = () => {
    try {
      const files = readdirSync(workDir)
        .filter((f) => f.startsWith('overlay-') && f.endsWith('.png'))
        .map((f) => ({ f, t: statSync(`${workDir}/${f}`).mtimeMs }))
        .sort((a, b) => b.t - a.t)
      return files.length ? `${workDir}/${files[0].f}` : null
    } catch {
      return null
    }
  }

  const server = createServer((req, res) => {
    const url = new URL(req.url || '/', 'http://127.0.0.1')
    if (req.method === 'OPTIONS') {
      res.writeHead(204, cors)
      res.end()
      return
    }
    if (url.pathname === '/status') {
      sendJson(res, {
        ok: true,
        stopped: hud.stopped,
        uptimeMs: Date.now() - hud.startedAt,
        lastScan: hud.lastScan,
        timeline: hud.timeline.slice(0, 12),
        pending: pendingList(),
        hudPort,
        // 诊断信息：UI 用它区分"没连上""没权限""没扫描过"
        platform: process.platform,
        engine: plan.engine,
        engineLabel: plan.engine === 'exe' ? 'Windows 引擎 (lens.exe)' : 'macOS 引擎 (Node)',
        enginePath: plan.engine === 'exe' ? plan.cmd : plan.pre[0],
        hudMode: hudNative ? 'native' : 'web',
        permission: hud.permission || null,
        permissions: permissionState
          ? {
              accessibility: permissionState.accessibility,
              screen: permissionState.screen,
              granted: permissionState.granted,
              missing: permissionState.missing,
              panes: permissionState.panes,
              hint: permissionState.hint,
            }
          : null,
        settings: {
          confirmClicks: settings.confirmClicks,
          humanConfirm: settings.humanConfirm,
          autoCheck: settings.autoCheck,
          checkBox: settings.checkBox,
          hudEnabled: settings.hudEnabled,
          humanize: settings.humanize,
        },
      })
      return
    }
    // 权限：GET 读当前状态，POST 重新申请（HUD 卡片与设置页的按钮都打这个口）
    if (url.pathname === '/permissions') {
      if (req.method === 'POST') {
        if (plan.engine === 'exe') {
          sendJson(res, { ok: true, granted: true, missing: [], hint: 'Windows 侧没有这套授权（用的是 UIA/SendInput）' })
          return
        }
        runRaw(['perm', '--action', 'request'])
          .then((r) => {
            permissionState = r
            hud.permission = r && r.granted === false ? { at: Date.now(), code: 'PERMISSION', message: r.hint } : null
            sendJson(res, r || { ok: false })
          })
          .catch((e) => sendJson(res, { ok: false, code: 'PERM_FAILED', message: String(e && e.message) }))
        return
      }
      sendJson(res, permissionState || { ok: true, granted: plan.engine === 'exe', pending: true })
      return
    }
    if (url.pathname === '/pending') {
      sendJson(res, { ok: true, pending: pendingList() })
      return
    }
    // 人工确认/取消：HUD 面板上的按钮走这两个口，跟模型调 computer_act {confirm} 是同一条实现
    if (url.pathname === '/confirm') {
      const token = String(url.searchParams.get('token') || '')
      if (!token) { sendJson(res, { ok: false, code: 'BAD_ARGS', message: 'missing token' }); return }
      confirmPending(token, 'human')
        .then((result) => sendJson(res, result))
        .catch((e) => sendJson(res, { ok: false, code: 'CONFIRM_FAILED', message: String(e && e.message) }))
      return
    }
    if (url.pathname === '/cancel') {
      const token = String(url.searchParams.get('token') || '')
      sendJson(res, cancelPending(token))
      return
    }
    // 预览图（HUD 待确认卡片里显示的那张绿框图）
    if (url.pathname === '/preview') {
      const token = String(url.searchParams.get('token') || '')
      const pending = pendingClicks.get(token)
      const file = pending && pending.image
      if (!file) {
        res.writeHead(404, cors)
        res.end('no such pending preview')
        return
      }
      try {
        const buf = readFileSync(file)
        res.writeHead(200, { ...cors, 'Content-Type': 'image/png' })
        res.end(buf)
      } catch {
        res.writeHead(500, cors)
        res.end('read failed')
      }
      return
    }
    if (url.pathname === '/stop' || url.pathname === '/resume') {
      hud.stopped = url.pathname === '/stop'
      record('hud', hud.stopped ? '急停已拉起' : '急停已解除', true)
      sendJson(res, { ok: true, stopped: hud.stopped })
      return
    }
    if (url.pathname === '/thumb') {
      const file = newestOverlay()
      if (!file) {
        res.writeHead(404, cors)
        res.end('no scan yet')
        return
      }
      try {
        const buf = readFileSync(file)
        res.writeHead(200, { ...cors, 'Content-Type': 'image/png' })
        res.end(buf)
      } catch {
        res.writeHead(500, cors)
        res.end('read failed')
      }
      return
    }
    res.writeHead(404, cors)
    res.end('not found')
  })
  server.on('error', () => {
    /* 端口被占用就算了，HUD 显示离线即可，不能拖垮插件 */
  })

  ctx.effect(() => {
    try {
      server.listen(hudPort, '127.0.0.1')
    } catch {
      /* ignore */
    }
    return () => {
      try {
        server.close()
      } catch {
        /* ignore */
      }
    }
  })
}

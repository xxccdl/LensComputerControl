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
function parseLoose(text) {
  const t = String(text || '').trim()
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
export function apply(ctx, config) {
  const cfg = config && typeof config === 'object' ? config : {}
  const settings = readSettings(cfg)
  const exePath = cfg.exePath || join(HERE, 'bin', 'lens.exe')
  const workDir = cfg.workDir || join(tmpdir(), 'lens-computer-control')
  const timeoutMs = settings.timeoutMs
  const hudPort = Number(unwrap(cfg.hudPort, 45871)) > 0 ? Number(unwrap(cfg.hudPort, 45871)) : 45871

  /** HUD 状态：动作时间线 + 急停闸。急停一旦拉起，所有 computer_* 工具立刻拒绝执行。 */
  const hud = {
    stopped: false,
    startedAt: Date.now(),
    timeline: [],
    lastScan: null,
  }

  const record = (kind, detail, ok) => {
    hud.timeline.unshift({ at: Date.now(), kind, detail, ok })
    if (hud.timeline.length > 40) hud.timeline.length = 40
  }

  /** 坐标点击的「预览 → 确认」：令牌 -> 那一次点击的参数。一次性、短时效。 */
  const pendingClicks = new Map()
  const PENDING_TTL = 180000

  try {
    mkdirSync(workDir, { recursive: true })
  } catch {
    /* workDir 可能已存在或无权限，运行期再报错 */
  }

  const runExe = (exe, args) =>
    new Promise((resolve) => {
      execFile(
        exe,
        args,
        { timeout: timeoutMs, windowsHide: true, maxBuffer: 64 * 1024 * 1024 },
        (err, stdout, stderr) => {
          const out = String(stdout || '').trim()
          if (out) {
            const parsed = parseLoose(out)
            if (parsed !== null) {
              resolve(parsed)
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

  const runRaw = (args) => runExe(exePath, args)

  // 首次调用时把视觉反馈层拉起来（渐变蓝边 + 顶部胶囊）。失败不影响主流程。
  let hudReady = false
  const ensureHudOnce = async () => {
    if (hudReady) return
    hudReady = true
    // 设置里关掉 HUD 就彻底不启动那个窗口：省一次进程，也让"完全静默"成为可选
    if (!settings.hudEnabled) return
    try {
      await runRaw(['hud', '--action', 'start'])
    } catch {
      /* HUD 只是锦上添花 */
    }
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
    record(verb, label.slice(0, 90), ok)
    if (verb === 'scan' && result && result.ok) {
      hud.lastScan = {
        at: Date.now(),
        window: result.window && result.window.title,
        level: result.capability && result.capability.level,
        candidates: (result.candidates || []).length,
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
  const confirmPending = async (token) => {
    const pending = pendingClicks.get(token)
    if (!pending) {
      return {
        ok: false,
        code: 'CONFIRM_INVALID',
        message: '确认令牌无效或已经用过（预览只能确认一次）',
        hint: '重新调一次 computer_act 只给坐标，会重新出图并给新令牌。',
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
      '列出当前所有可见的顶层窗口（标题、hwnd、DPI、进程、最小化状态、精确可见边框）。' +
      '在做任何点击之前先用它拿到目标窗口，后续所有操作都用窗口标题的稳定片段或 hwnd 定位。',
    parameters: {
      type: 'object',
      properties: {
        filter: { type: 'string', description: '可选，只返回标题或进程名包含该子串的窗口' },
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
      const list = (await run(['info', '--json'])) || []
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
      '扫描一个窗口，产出「可点击候选表」并渲染一张带编号的标注图。**有文字控件的界面优先用它**——一次就能拿到全部候选。\n但如果候选里没有你要点的东西、或返回里出现「扫描质量差」的警告，**立刻改用 computer_vision**，不要重扫。\n' +
      '候选来自多通道：uia(无障碍树) / solid(实心色块按钮) / ocr(文字) / table(表格行列) / template(已教过的锚点)。' +
      '自绘窗口（微信、QQ、券商行情、Java 客户端）通常 UIA 为空，靠 solid+ocr+table 兜住。\n' +
      '返回的每个候选带 id、通道、类型、屏幕物理坐标 screen{cx,cy}、文字与颜色。' +
      '**你必须只用候选的 id 来指定点击目标，绝不要自己编造坐标。**\n' +
      'overlay 字段是一张标注图路径，需要看图确认时用 read_image 打开它。\n' +
      '【微信/QQ 这类 Qt 自绘窗口】uia 基本为空（节点 <10），候选主要来自 ocr：一次约 20–40 个，' +
      '够用但**文字会认错**（老师→老丝、取消→取肖、语音通话→浯音通话），所以 find=text 要写短一点的关键词、' +
      '不要拿整句去精确匹配。会话列表在左、消息区在右；找不到某个联系人时先 scroll 列表或直接用搜索框，别硬扫。',
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
      '对窗口执行一个动作。三种最常用形态：\n' +
      '1) 点候选：{window, id} —— 强烈推荐，坐标由引擎按候选框计算。\n' +
      '2) 发键：{window, combo} —— 如 "ctrl+a"、"enter"、"alt+tab"，键盘流往往比点击更可靠。\n' +
      '3) 输入文字：{window, text} —— 支持中文（Unicode 注入）。\n' +
      '4) 粘贴：{window, action:"paste", text} —— **超过 20 个字符优先用这个**：一次原子操作，' +
      '不怕中途焦点漂移；完成后会自动还原你原来的剪贴板。\n' +
      '\n【坐标点击默认只预览、不落点】给 x/y 点击时，工具**不会先点**：它跑一次坐标检验，' +
      '回一张把命中范围画成绿框（+右下角放大镜）的图，并给一个 `ck_xxx` 确认令牌。' +
      '你（或用户）看图确认位置对，再调 `{ window, confirm: "ck_xxx" }` 才真正点击；' +
      '要换位置就重新给坐标再预览。令牌一次性、180 秒有效。用 `id` / `cell` 点击不受此限（坐标是引擎算的）。\n' +
      '可选 background=true：用窗口消息后台点击，不抢焦点、不动真实鼠标（对 Qt/Win32 自绘窗口常常有效）。\n' +
      '默认会先把目标窗口强制前台（系统可能拒绝，返回里的 notes 会说明），然后再点。\n' +
      '【微信】点会话/联系人优先用 {id}（来自 computer_scan 的候选编号），比坐标稳；' +
      '发消息时先点输入框再 {text:"…"} 再 {combo:"enter"}。' +
      '窗口点完消失（比如登录成功后登录窗关闭）会返回 warning:"window_gone"，那是成功不是失败。' +
      '登录后主窗是**新的 hwnd**，别继续用登录窗的句柄，用 window:"微信" 重新定位。\n' +
      '【点不准时】不要反复试坐标；没有可用候选（图标/画布/游戏）就用 computer_vision 定位后再回来点。',
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
          lines.push(value.verify.changed ? '核验：画面有变化，动作生效' : '核验：画面没变化（changed=false）——这一下可能没生效，换策略而不是重复点')
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
        const chk = await runExe(exePath, [
          'check', '--window', w,
          '--x', String(Math.round(args.x)), '--y', String(Math.round(args.y)),
          '--w', String(settings.checkBox), '--h', String(settings.checkBox),
          '--zoom', String(settings.checkZoom),
          '--out', `${workDir.replace(/\\/g, '/')}/autocheck-${stamp}.png`,
        ])
        record('check', `auto (${args.x},${args.y})`, chk && chk.ok !== false)
        if (chk && chk.ok === true && (chk.covered || chk.inside === false)) {
          return {
            ok: false,
            code: chk.inside === false ? 'POINT_OUTSIDE' : 'TARGET_COVERED',
            message: chk.inside === false ? '坐标落在窗口外，已按设置拒绝点击' : '目标点被其他窗口遮挡，已按设置拒绝点击',
            image: chk.image,
            point: chk.point,
            coveredBy: chk.coveredBy,
            hint: '看图修正坐标，或先 focus 目标窗口；也可以在 设置 → 插件 → Lens 关掉「自动坐标检验」。',
          }
        }
      }

      // ---------------------------------------------------------- 坐标点击：先预览，不落点
      // 默认行为（设置 confirmClicks=true）：模型给 x/y 时**先不点**，跑一次坐标检验，
      // 回一张把命中范围画成绿框的图 + 一次性确认令牌；模型/用户确认后再用
      // computer_act { confirm: "<token>" } 真正点击。id / cell 用的是引擎算出来的坐标，
      // 不需要预览（那本来就是引擎的答案，不是模型的猜测）。
      const isCoordClick = args?.id === undefined && !args?.cell

      if (args?.confirm) return confirmPending(String(args.confirm))

      if (isCoordClick && settings.confirmClicks && args?.force !== true) {
        const stamp = Date.now().toString(36)
        const image = `${workDir.replace(/\\/g, '/')}/preclick-${stamp}.png`
        const px = Math.round(args.x)
        const py = Math.round(args.y)
        const chk = await runExe(exePath, [
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
      '**纯视觉坐标定位**，不依赖任何识别通道。三种模式：\n' +
      'mode="cells"（默认）：把屏幕/窗口切成 N 格，每格印着屏幕绝对坐标，' +
      '你用 read_image 看图、报格子编号；再传 path 就只切那一格，逐步收窄直到够准。\n' +
      'mode="grid"：给某个窗口盖一层轴刻度网格（列号/行号），返回标注图。\n' +
      'mode="zoom"：把 grid 的某个格子放大并叠二级网格，返回每个子格子的精确屏幕坐标。\n' +
      '\n【什么时候必须用它 —— 不要硬用 computer_scan 反复试】\n' +
      '· scan 一个候选都没给出，或候选里根本没有你要点的东西\n' +
      '· 扫描显示 uia=不可用 且候选很少（<8）——识别通道基本失灵\n' +
      '· 目标是**没有文字的东西**：图标、图形按钮、开关、滑块、画布、游戏、远程桌面\n' +
      '· 需要很精确的落点（误差 <30 像素），而这些对象没有可用的元素条目\n' +
      '**判断法则：scan 返回的候选和你要找的东西对不上，别重扫第二次，直接换视觉定位。**\n' +
      '\n【效率技巧】\n' +
      '· cells=9（3×3）时每级格子形状完全不变，最稳、推荐；cells=10 也能用但长宽比逐级翻转\n' +
      '· 已知大概位置就用 rect:"x,y,w,h" 直接框那块，比逐级下钻省两次往返\n' +
      '· 拿不准就 mark:"x,y" 画红色十字准星，一眼看出目标落在哪格、偏多少\n' +
      '· 做到够准后 computer_act {x,y} 点击，坐标就是格子给的 cx,cy',
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
      return run(argv)
    },
  }

  // ---------------------------------------------------------------- tool 3.5
  const checkTool = {
    name: 'computer_check',
    description:
      '坐标检验（只读，不点不按）：把你要点的坐标交给它，回一张截图 —— 你要点的那一块被画成**绿色框**（默认 24x24，鼠标热区大小），' +
      '右下角贴一张同一处的**放大镜**（最近邻放大 + 8px 像素格）。看图就知道这个坐标到底压住了哪个字、哪个图标、还是边缘。\n' +
      '\n【什么时候用】\n' +
      '· 坐标是你自己算的 / 从 computer_vision 格子里拿的 / 从图里目测的 —— 点之前先 check 一次\n' +
      '· 点了没反应、或者怕点到隔壁按钮时\n' +
      '· 想确认某个坐标是不是落在刚 scan 出来的某个候选上（会直接告诉你 #id 和文字）\n' +
      '\n【看什么】\n' +
      '· 绿框正中间是不是你要的目标；偏了就把偏差加减到坐标上再 check 一次\n' +
      '· JSON 里 `covered:true` = 这一点此刻被别的窗口盖住（直接点会打错窗口）\n' +
      '· JSON 里 `inside:false` = 坐标落在窗口外，重新取点\n' +
      '· `candidate` 有值 = 这个点命中了上次 scan 的某条候选，点它更稳\n' +
      '\n它不改任何状态，也不移动鼠标。确认没问题后再 computer_act 点下去。',
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
      const result = await runExe(exePath, argv)
      record('check', `${argv[2]},${argv[4]}`, result && result.ok !== false)
      return result
    },
  }

  // ---------------------------------------------------------------- tool 4
  const runTool = {
    name: 'computer_run',
    description:
      '一次调用执行多步电脑控制（脚本自动化），省掉 N 次往返。每行一个动作，格式「动词 键=值 …」：\n' +
      '  window <标题>         设定目标窗口（后续坐标可写 w 前缀表示相对客户区）\n' +
      '  focus                 把目标窗口置前\n' +
      '  click x=100 y=200 / click find=text:确定 / click id=6 count=2\n' +
      '  type text=你好\n' +
      '  key combo=ctrl+a\n' +
      '  scroll x=500 y=400 delta=-3 times=2\n' +
      '  drag from=w100,w100 to=w300,w400 duration=600\n' +
      '  wait for=changed|exists|text:xxx timeout=5000\n' +
      '  verify expect=changed\n' +
      '  sleep ms=500\n' +
      '遇到失败默认停下并返回每一步的 trace；加 continue=true 则失败也继续。\n' +
      '适合「打开→输入→回车→截图核验」这类固定套路，比逐步调用快得多也更稳。\n' +
      '【微信发消息】推荐走搜索框，比在会话列表里翻找可靠得多：\n' +
      '  window 微信 / focus / key combo=ctrl+f / type text=张三 / key combo=enter\n' +
      '  sleep ms=400 / type text=在吗 / key combo=enter / wait for=changed timeout=3000\n' +
      '【微信已知坑】find=text 要用短关键词（OCR 会认错字）；登录窗和主窗是两个不同 hwnd，' +
      '脚本里用 window 标题而不是 hwnd；窗口在托盘时先 focus（会自动还原），不要重开应用。',
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
      '窗口管理：最大化 / 最小化 / 还原 / 关闭 / 移动缩放 / 置顶。\n' +
      '自动化前先把窗口摆到确定的位置和大小很重要——否则坐标和截图会随用户拖动而变。\n' +
      'action=move 时给 x/y/w/h（屏幕像素，不传则沿用当前值）。\n' +
      '**故意不提供 close**：关闭应用是不可逆操作，误选一次就可能丢掉用户未保存的工作，需要关程序请让用户自己来。',
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
      const argv = ['window', '--window', w, '--action', act]
      for (const k of ['x', 'y', 'w', 'h']) if (args?.[k] !== undefined) argv.push(`--${k}`, String(args[k]))
      return run(argv)
    },
  }

  const lookTool = {
    name: 'computer_look',
    description:
      '**看一眼**：mode="image" 截取窗口/桌面得到干净原图（用 read_image 直接看布局）；' +
      'mode="text" 读某个矩形区域的文字（OCR）。\n' +
      '和 computer_scan 的区别：scan 返回候选表+标注图，look 给的是**没有标注的原始画面**，' +
      '适合判断"这界面上到底有什么"、确认区域长相、或读出列表内容。\n' +
      'image 模式返回的 x,y 是图片左上角在屏幕上的坐标：图内像素 (px,py) → 屏幕 (x+px, y+py)。\n' +
      'text 模式的 rect 是**相对窗口客户区**的 "x,y,w,h"。OCR 会认错字（老师→老丝），关键值别整句比对。',
    parameters: {
      type: 'object',
      properties: {
        window: { type: 'string', description: '窗口标题片段或 hwnd；image 模式留空则截整个桌面' },
        mode: { type: 'string', enum: ['image', 'text'], description: 'image=截图（默认），text=读文字' },
        rect: { type: 'string', description: 'text 模式下相对窗口的 "x,y,w,h"，留空读整个窗口' },
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
      if (mode === 'text') {
        if (!w) return { ok: false, code: 'BAD_ARGS', message: 'text 模式需要 window 参数' }
        const argv = ['read', '--window', w, '--json']
        argv.push('--rect', args?.rect ? String(args.rect) : '0,0,99999,99999')
        if (args?.expect !== undefined) argv.push('--expect', String(args.expect))
        return run(argv)
      }
      const stamp = Date.now().toString(36)
      const out = `${workDir.replace(/\\/g, '/')}/shot-${stamp}.png`
      const argv = ['cap', '--out', out]
      if (w) argv.push('--window', w)
      else argv.push('--desktop')
      return run(argv)
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
      })
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
      confirmPending(token)
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

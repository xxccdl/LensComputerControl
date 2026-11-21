#!/usr/bin/env node
/**
 * engine-mac.mjs —— Lens 的 macOS 引擎（实验性，零第三方依赖）。
 *
 * 为什么另写一个引擎：Windows 侧是随包分发的 lens.exe（C# / Win32 + UIA + WinRT OCR）。
 * macOS 上没有同等的一条"系统自带接口"，但自带三样够用的东西：
 *   · osascript（JXA）   → Accessibility 树、窗口列表、点击/按键/输入
 *   · screencapture      → 截图（可按窗口矩形截）
 *   · pbcopy / pbpaste   → 粘贴（先存用户原剪贴板，粘完写回）
 * 于是这里用 Node 直接调这些系统组件，命令名与参数尽量与 lens.exe 对齐，
 * 上层 index.js 只按平台换"可执行文件"，工具层代码不需要分叉。
 *
 * 已知限制（实验性，不要在生产上依赖）：
 *   · 需要用户在「系统设置 → 隐私与安全性 → 辅助功能 / 屏幕录制」给宿主进程授权；
 *   · AX 树在 Electron / Qt / 游戏里通常为空，此时只能走计算机视觉切块 + check 预览；
 *   · 没有 HUD 浮层（lenshud.exe 是 Windows 实现），HUD 只在 Web 面板里；
 *   · scroll / drag / 遮挡检测尚未实现，会返回 NOT_IMPLEMENTED。
 */
import { execFile, spawn } from 'node:child_process'
import { mkdirSync, writeFileSync } from 'node:fs'
import { readFile } from 'node:fs/promises'
import { dirname, join } from 'node:path'
import { pathToFileURL } from 'node:url'
import { promisify } from 'node:util'
import { deflateSync, inflateSync } from 'node:zlib'

const run = promisify(execFile)
const TMP = process.env.TMPDIR || '/tmp'

// ---------------------------------------------------------------- 参数解析
function arg(argv, name, def) {
  const i = argv.indexOf(name)
  return i >= 0 && i + 1 < argv.length ? argv[i + 1] : def
}
function intArg(argv, name, def) {
  const v = arg(argv, name, null)
  const n = v === null ? NaN : parseInt(v, 10)
  return Number.isFinite(n) ? n : def
}
const flag = (argv, name) => argv.includes(name)

// ---------------------------------------------------------------- JXA 通道
const JXA_PRELUDE =
  'function sysEvents() { return Application("System Events"); }\nfunction out(o) { return JSON.stringify(o); }\n'

async function jxa(script) {
  const { stdout } = await run('osascript', ['-l', 'JavaScript', '-e', JXA_PRELUDE + script], {
    maxBuffer: 32 * 1024 * 1024,
  })
  return String(stdout || '').trim()
}

/** 列出窗口：标题、进程、pid、位置、尺寸、是否最小化。 */
async function listWindows() {
  const script = `
    const se = sysEvents();
    const result = [];
    const procs = se.applicationProcesses.whose({ backgroundOnly: false })();
    for (const proc of procs) {
      let wins;
      try { wins = proc.windows(); } catch (e) { continue; }
      for (const w of wins) {
        let p = null, s = null, nm = '', mn = false;
        try { p = w.position(); } catch (e) {}
        try { s = w.size(); } catch (e) {}
        try { nm = w.name(); } catch (e) {}
        try { mn = w.minimized(); } catch (e) {}
        result.push({
          title: String(nm || ''),
          process: proc.name(),
          pid: (function () { try { return proc.unixId(); } catch (e) { return 0; } })(),
          x: p ? p[0] : 0, y: p ? p[1] : 0,
          w: s ? s[0] : 0, h: s ? s[1] : 0,
          minimized: !!mn,
        });
      }
    }
    out(result);
  `
  try {
    return JSON.parse((await jxa(script)) || '[]')
  } catch {
    return []
  }
}

function pickWindow(list, key) {
  if (!key) return null
  const k = String(key).toLowerCase()
  const exact = list.filter((w) => String(w.title).toLowerCase() === k)
  if (exact.length) return exact[0]
  const fuzzy = list.filter(
    (w) => String(w.title).toLowerCase().includes(k) || String(w.process).toLowerCase().includes(k),
  )
  return fuzzy.length ? fuzzy[0] : null
}

/** Accessibility 树 → 候选（对应 Windows 侧的 uia 通道）。 */
async function axCandidates(pid) {
  const script = `
    const se = sysEvents();
    const proc = se.applicationProcesses.whose({ unixId: ${pid} })[0];
    if (!proc) { out([]); } else {
      const found = [];
      function walk(el, depth) {
        if (!el || depth > 12 || found.length > 400) return;
        let role = '', name = '', desc = '', pos = null, size = null;
        try { role = el.role(); } catch (e) {}
        try { name = el.name(); } catch (e) {}
        try { desc = el.description(); } catch (e) {}
        try { pos = el.position(); } catch (e) {}
        try { size = el.size(); } catch (e) {}
        if (pos && size && size[0] > 1 && size[1] > 1) {
          const clickable = /AXButton|AXLink|AXMenuItem|AXTextField|AXTextArea|AXCheckBox|AXRadioButton|AXTab|AXCell|AXPopUpButton|AXComboBox|AXSlider/.test(role);
          if (clickable || name) {
            found.push({ role: role, name: String(name || desc || '').slice(0, 80), x: pos[0], y: pos[1], w: size[0], h: size[1] });
          }
        }
        let kids = [];
        try { kids = el.uiElements(); } catch (e) { kids = []; }
        for (const k of kids) walk(k, depth + 1);
      }
      walk(proc, 0);
      out(found);
    }
  `
  try {
    return JSON.parse((await jxa(script)) || '[]')
  } catch {
    return []
  }
}

// ---------------------------------------------------------------- 截图
async function screenRect() {
  try {
    const r = JSON.parse(
      (await jxa(`
        ObjC.import('AppKit');
        const f = $.NSScreen.mainScreen.frame;
        out({ x: 0, y: 0, w: f.size.width, h: f.size.height });
      `)) || '{}',
    )
    if (r && r.w > 0) return { x: 0, y: 0, w: Math.round(r.w), h: Math.round(r.h) }
  } catch {}
  return { x: 0, y: 0, w: 1920, h: 1080 }
}

async function captureRect(rect) {
  const tmp = join(TMP, `lens-cap-${process.pid}-${Date.now()}.png`)
  const args = ['-x', '-o', '-t', 'png']
  if (rect) args.push('-R', `${rect.x},${rect.y},${rect.w},${rect.h}`)
  args.push(tmp)
  await run('screencapture', args)
  return readFile(tmp)
}

// ---------------------------------------------------------------- PNG 编解码（零依赖）
/** 只支持 8bit RGB/RGBA、非隔行 —— screencapture 的输出就是这种。 */
function decodePng(buf) {
  if (buf.readUInt32BE(0) !== 0x89504e47) throw new Error('not a png')
  let off = 8
  let width = 0
  let height = 0
  let bitDepth = 8
  let colorType = 6
  const idat = []
  while (off < buf.length) {
    const len = buf.readUInt32BE(off)
    const type = buf.toString('ascii', off + 4, off + 8)
    const data = buf.subarray(off + 8, off + 8 + len)
    if (type === 'IHDR') {
      width = data.readUInt32BE(0)
      height = data.readUInt32BE(4)
      bitDepth = data[8]
      colorType = data[9]
      if (bitDepth !== 8 || (colorType !== 2 && colorType !== 6)) {
        throw new Error(`unsupported png: depth=${bitDepth} color=${colorType}`)
      }
    } else if (type === 'IDAT') {
      idat.push(data)
    } else if (type === 'IEND') break
    off += 12 + len
  }
  const raw = inflateSync(Buffer.concat(idat))
  const channels = colorType === 6 ? 4 : 3
  const stride = width * channels
  const out = Buffer.alloc(height * stride)
  let pos = 0
  for (let y = 0; y < height; y++) {
    const filter = raw[pos++]
    const line = raw.subarray(pos, pos + stride)
    pos += stride
    const cur = out.subarray(y * stride, (y + 1) * stride)
    for (let x = 0; x < stride; x++) {
      const a = x >= channels ? cur[x - channels] : 0
      const b = y > 0 ? out[(y - 1) * stride + x] : 0
      const c = x >= channels && y > 0 ? out[(y - 1) * stride + x - channels] : 0
      let v = line[x]
      if (filter === 1) v += a
      else if (filter === 2) v += b
      else if (filter === 3) v += (a + b) >> 1
      else if (filter === 4) {
        const p = a + b - c
        const pa = Math.abs(p - a)
        const pb = Math.abs(p - b)
        const pc = Math.abs(p - c)
        v += pa <= pb && pa <= pc ? a : pb <= pc ? b : c
      }
      cur[x] = v & 0xff
    }
  }
  return { width, height, channels, data: out }
}

const CRC_TABLE = (() => {
  const t = new Int32Array(256)
  for (let n = 0; n < 256; n++) {
    let c = n
    for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1
    t[n] = c
  }
  return t
})()

function crc32(buf) {
  let c = -1
  for (let i = 0; i < buf.length; i++) c = (c >>> 8) ^ CRC_TABLE[(c ^ buf[i]) & 0xff]
  return (c ^ -1) >>> 0
}

function chunk(type, data) {
  const len = Buffer.alloc(4)
  len.writeUInt32BE(data.length)
  const body = Buffer.concat([Buffer.from(type, 'ascii'), data])
  const crc = Buffer.alloc(4)
  crc.writeUInt32BE(crc32(body))
  return Buffer.concat([len, body, crc])
}

function encodePng(img) {
  const { width, height, channels, data } = img
  const stride = width * channels
  const raw = Buffer.alloc((stride + 1) * height)
  for (let y = 0; y < height; y++) {
    raw[y * (stride + 1)] = 0
    data.copy(raw, y * (stride + 1) + 1, y * stride, (y + 1) * stride)
  }
  const ihdr = Buffer.alloc(13)
  ihdr.writeUInt32BE(width, 0)
  ihdr.writeUInt32BE(height, 4)
  ihdr[8] = 8
  ihdr[9] = channels === 4 ? 6 : 2
  return Buffer.concat([
    Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]),
    chunk('IHDR', ihdr),
    chunk('IDAT', deflateSync(raw, { level: 6 })),
    chunk('IEND', Buffer.alloc(0)),
  ])
}

function fillRect(img, x, y, w, h, rgba) {
  const { width, height, channels, data } = img
  const [r, g, b, a = 255] = rgba
  const alpha = a / 255
  for (let yy = Math.max(0, y); yy < Math.min(height, y + h); yy++) {
    for (let xx = Math.max(0, x); xx < Math.min(width, x + w); xx++) {
      const i = (yy * width + xx) * channels
      data[i] = Math.round(data[i] * (1 - alpha) + r * alpha)
      data[i + 1] = Math.round(data[i + 1] * (1 - alpha) + g * alpha)
      data[i + 2] = Math.round(data[i + 2] * (1 - alpha) + b * alpha)
      if (channels === 4) data[i + 3] = 255
    }
  }
}

function strokeRect(img, x, y, w, h, rgba, thickness = 2) {
  fillRect(img, x, y, w, thickness, rgba)
  fillRect(img, x, y + h - thickness, w, thickness, rgba)
  fillRect(img, x, y, thickness, h, rgba)
  fillRect(img, x + w - thickness, y, thickness, h, rgba)
}

function pixelAt(img, x, y) {
  const xx = Math.max(0, Math.min(img.width - 1, Math.round(x)))
  const yy = Math.max(0, Math.min(img.height - 1, Math.round(y)))
  const i = (yy * img.width + xx) * img.channels
  return [img.data[i], img.data[i + 1], img.data[i + 2]]
}

// ---------------------------------------------------------------- 命令实现
const emit = (obj) => process.stdout.write(JSON.stringify(obj))

async function cmdInfo(argv) {
  const filter = String(arg(argv, '--filter', '') || '').toLowerCase()
  const list = await listWindows()
  const mapped = list
    .map((w, i) => ({
      hwnd: w.pid * 1000 + i, // macOS 没有 hwnd，用 pid*1000+序号造一个稳定 id
      title: w.title,
      process: w.process,
      pid: w.pid,
      dpi: 144,
      frame: { x: w.x, y: w.y, w: w.w, h: w.h },
      client: { x: w.x, y: w.y, w: w.w, h: w.h },
      visible: true,
      minimized: !!w.minimized,
      maximized: false,
    }))
    .filter((w) => !filter || `${w.title} ${w.process}`.toLowerCase().includes(filter))
  if (flag(argv, '--json')) {
    emit(mapped)
    return 0
  }
  for (const w of mapped) {
    console.log(`${w.hwnd}  ${w.process}  ${w.title}  ${w.frame.w}x${w.frame.h} @${w.frame.x},${w.frame.y}`)
  }
  return 0
}

async function cmdScan(argv) {
  const key = arg(argv, '--window', null)
  const list = await listWindows()
  const win = key ? pickWindow(list, key) : null
  if (!win) {
    emit({ ok: false, code: 'WINDOW_NOT_FOUND', message: `window not found: ${key}` })
    return 3
  }
  const cands = await axCandidates(win.pid)
  const candidates = cands.map((c, i) => ({
    id: i + 1,
    channel: 'ax',
    kind: /Button/.test(c.role) ? 'button' : /Text/.test(c.role) ? 'text' : 'unknown',
    box: { x: c.x, y: c.y, w: c.w, h: c.h },
    screen: {
      x: c.x,
      y: c.y,
      w: c.w,
      h: c.h,
      cx: Math.round(c.x + c.w / 2),
      cy: Math.round(c.y + c.h / 2),
    },
    text: c.name || null,
    score: 0.8,
  }))

  const overlay = arg(argv, '--overlay', null)
  if (overlay) {
    try {
      const frame = { x: Math.round(win.x), y: Math.round(win.y), w: Math.round(win.w), h: Math.round(win.h) }
      const img = decodePng(await captureRect(frame))
      for (const c of candidates) {
        strokeRect(img, c.box.x - frame.x, c.box.y - frame.y, c.box.w, c.box.h, [0, 200, 255, 255], 2)
      }
      mkdirSync(dirname(overlay), { recursive: true })
      writeFileSync(overlay, encodePng(img))
    } catch {
      /* 截图失败不影响候选表 */
    }
  }

  emit({
    ok: true,
    window: {
      hwnd: win.pid * 1000,
      title: win.title,
      process: win.process,
      pid: win.pid,
      frame: { x: win.x, y: win.y, w: win.w, h: win.h },
    },
    capability: { level: candidates.length ? 'L2' : 'L1', capturable: true, channel: 'ax' },
    candidates,
    overlay: overlay || null,
    notes: ['engine=mac', `ax=${candidates.length}`].concat(
      candidates.length ? [] : ['AX 树为空：改用 computer_vision 切块定位'],
    ),
  })
  return 0
}

async function cmdCheck(argv) {
  const x = intArg(argv, '--x', null)
  const y = intArg(argv, '--y', null)
  if (x === null || y === null) {
    emit({ ok: false, code: 'BAD_ARGS', message: 'need --x and --y' })
    return 2
  }
  const bw = intArg(argv, '--w', 24)
  const bh = intArg(argv, '--h', bw)
  const out = arg(argv, '--out', join(process.cwd(), 'check.png'))
  const pad = Math.max(120, Math.max(bw, bh) * 4)
  const rect = { x: Math.round(x - pad), y: Math.round(y - pad), w: pad * 2, h: pad * 2 }
  const img = decodePng(await captureRect(rect))

  const lx = x - rect.x
  const ly = y - rect.y
  const box = { x: lx - Math.round(bw / 2), y: ly - Math.round(bh / 2), w: bw, h: bh }

  strokeRect(img, box.x, box.y, box.w, box.h, [34, 197, 94, 255], 2)
  fillRect(img, lx - 10, ly, 21, 1, [34, 197, 94, 255])
  fillRect(img, lx, ly - 10, 1, 21, [34, 197, 94, 255])

  const zoom = Math.max(2, Math.min(8, intArg(argv, '--zoom', 6)))
  const cropPad = Math.max(20, Math.round(Math.max(bw, bh) / 2) + 12)
  const cx0 = Math.max(0, box.x - cropPad)
  const cy0 = Math.max(0, box.y - cropPad)
  const crop = {
    x: cx0,
    y: cy0,
    w: Math.min(img.width - cx0, cropPad * 2 + box.w),
    h: Math.min(img.height - cy0, cropPad * 2 + box.h),
  }
  const zw = crop.w * zoom
  const zh = crop.h * zoom
  const px = Math.max(0, img.width - zw - 8)
  const py = Math.max(0, img.height - zh - 8)
  for (let yy = 0; yy < zh; yy++) {
    for (let xx = 0; xx < zw; xx++) {
      const [r, g, b] = pixelAt(img, crop.x + Math.floor(xx / zoom), crop.y + Math.floor(yy / zoom))
      fillRect(img, px + xx, py + yy, 1, 1, [r, g, b, 255])
    }
  }
  strokeRect(img, px, py, zw, zh, [255, 255, 255, 255], 2)

  mkdirSync(dirname(out), { recursive: true })
  writeFileSync(out, encodePng(img))
  const [pr, pg, pb] = pixelAt(img, lx, ly)
  emit({
    ok: true,
    image: out,
    point: { x, y },
    box: {
      x: x - Math.round(bw / 2),
      y: y - Math.round(bh / 2),
      w: bw,
      h: bh,
      x1: x - Math.round(bw / 2),
      y1: y - Math.round(bh / 2),
      x2: x - Math.round(bw / 2) + bw,
      y2: y - Math.round(bh / 2) + bh,
    },
    inside: true,
    covered: false,
    zoom,
    pixel: '#' + [pr, pg, pb].map((v) => v.toString(16).padStart(2, '0').toUpperCase()).join(''),
    hint: 'macOS 引擎：绿框是命中范围，右下角是放大视图；确认后 computer_act {confirm} 落点。',
    notes: ['engine=mac'],
  })
  return 0
}

async function cmdClick(argv) {
  const x = intArg(argv, '--x', null)
  const y = intArg(argv, '--y', null)
  if (arg(argv, '--id', null) !== null && (x === null || y === null)) {
    emit({ ok: false, code: 'ID_UNSUPPORTED', message: 'macOS 引擎暂不支持按候选 id 点击，请用 scan 给的 cx,cy' })
    return 4
  }
  if (x === null || y === null) {
    emit({ ok: false, code: 'BAD_ARGS', message: 'need --x and --y' })
    return 2
  }
  await jxa(`const se = sysEvents(); se.click({ at: [${x}, ${y}] }); out({ ok: true });`)
  emit({ ok: true, x, y, button: arg(argv, '--button', 'left'), notes: ['engine=mac', 'via System Events click'] })
  return 0
}

async function cmdType(argv) {
  const text = arg(argv, '--text', '')
  await jxa(`const se = sysEvents(); se.keystroke(${JSON.stringify(text)}); out({ ok: true });`)
  emit({ ok: true, chars: text.length, notes: ['engine=mac'] })
  return 0
}

const MAC_MODS = {
  cmd: 'command',
  command: 'command',
  ctrl: 'control',
  control: 'control',
  alt: 'option',
  option: 'option',
  shift: 'shift',
}

async function cmdKey(argv) {
  const combo = arg(argv, '--combo', '')
  const parts = combo.split('+').map((s) => s.trim()).filter(Boolean)
  const key = parts.pop() || ''
  const mods = parts.map((m) => MAC_MODS[m.toLowerCase()] || m)
  await jxa(
    `const se = sysEvents(); se.keystroke(${JSON.stringify(key)}, { using: ${JSON.stringify(mods)} }); out({ ok: true });`,
  )
  emit({ ok: true, combo, notes: ['engine=mac'] })
  return 0
}

function pbcopy(text) {
  return new Promise((resolve) => {
    const p = spawn('pbcopy', [])
    p.on('close', resolve)
    p.on('error', resolve)
    p.stdin.end(text)
  })
}

async function cmdPaste(argv) {
  const text = arg(argv, '--text', '')
  let saved = ''
  try {
    saved = String((await run('pbpaste', [])).stdout || '')
  } catch {
    saved = ''
  }
  await pbcopy(text)
  await jxa(`const se = sysEvents(); se.keystroke('v', { using: ['command'] }); out({ ok: true });`)
  await pbcopy(saved)
  emit({ ok: true, chars: text.length, notes: ['engine=mac', 'via pbcopy + cmd+v', 'clipboard restored'] })
  return 0
}

async function cmdFocus(argv) {
  const key = arg(argv, '--window', null)
  const raw = await jxa(`
    const se = sysEvents();
    const procs = se.applicationProcesses.whose({ name: ${JSON.stringify(key || '')} })();
    if (procs.length) { procs[0].frontmost = true; out({ ok: true }); } else { out({ ok: false }); }
  `)
  const ok = /true/.test(raw)
  emit({ ok, notes: ['engine=mac'] })
  return ok ? 0 : 3
}

async function cmdWindow(argv) {
  const action = arg(argv, '--action', 'info')
  const key = arg(argv, '--window', null)
  const list = await listWindows()
  const win = key ? pickWindow(list, key) : null
  if (!win) {
    emit({ ok: false, code: 'WINDOW_NOT_FOUND', message: `window not found: ${key}` })
    return 3
  }
  if (action === 'info') {
    emit({
      ok: true,
      window: { title: win.title, process: win.process, pid: win.pid, frame: { x: win.x, y: win.y, w: win.w, h: win.h } },
    })
    return 0
  }
  const x = intArg(argv, '--x', null)
  const y = intArg(argv, '--y', null)
  const w = intArg(argv, '--w', null)
  const h = intArg(argv, '--h', null)
  let body = ''
  if (action === 'focus') body = 'p.frontmost = true;'
  else if (action === 'minimize') body = 'p.windows[0].minimized = true;'
  else if (action === 'restore') body = 'p.windows[0].minimized = false; p.frontmost = true;'
  else if (action === 'move') {
    body = `if (p.windows.length) { const w0 = p.windows[0];
      w0.position = [${x === null ? win.x : x}, ${y === null ? win.y : y}];
      w0.size = [${w === null ? win.w : w}, ${h === null ? win.h : h}]; }`
  } else {
    emit({ ok: false, code: 'NOT_IMPLEMENTED', message: `macOS 引擎暂不支持 window action=${action}` })
    return 4
  }
  await jxa(`
    const se = sysEvents();
    const procs = se.applicationProcesses.whose({ name: ${JSON.stringify(win.process)} })();
    if (procs.length) { const p = procs[0]; ${body} out({ ok: true }); } else { out({ ok: false }); }
  `)
  emit({ ok: true, action, notes: ['engine=mac'] })
  return 0
}

async function cmdCap(argv) {
  const out = arg(argv, '--out', join(process.cwd(), 'cap.png'))
  const key = arg(argv, '--window', null)
  const list = await listWindows()
  const win = key ? pickWindow(list, key) : null
  const frame = win ? { x: Math.round(win.x), y: Math.round(win.y), w: Math.round(win.w), h: Math.round(win.h) } : null
  const buf = await captureRect(frame)
  mkdirSync(dirname(out), { recursive: true })
  writeFileSync(out, buf)
  emit({ ok: true, file: out, notes: ['engine=mac'] })
  return 0
}

async function cmdHud() {
  emit({ ok: false, code: 'HUD_UNAVAILABLE', message: 'macOS 引擎没有 HUD 浮层，请用 Web 面板的状态卡' })
  return 0
}

// ---------------------------------------------------------------- vision：切块递归定位
// 3×5 点阵数字，够画格子编号与坐标（不引入字体依赖）
const GLYPHS = {
  '0': ['111', '101', '101', '101', '111'],
  '1': ['010', '110', '010', '010', '111'],
  '2': ['111', '001', '111', '100', '111'],
  '3': ['111', '001', '111', '001', '111'],
  '4': ['101', '101', '111', '001', '001'],
  '5': ['111', '100', '111', '001', '111'],
  '6': ['111', '100', '111', '101', '111'],
  '7': ['111', '001', '001', '001', '001'],
  '8': ['111', '101', '111', '101', '111'],
  '9': ['111', '101', '111', '001', '111'],
  ',': ['000', '000', '000', '010', '010'],
  '#': ['101', '111', '101', '111', '101'],
  '>': ['100', '010', '001', '010', '100'],
}

/** 画一行点阵文字，scale 为像素放大倍数。 */
function drawText(img, x, y, text, scale, rgba) {
  let cursor = x
  for (const ch of String(text)) {
    const g = GLYPHS[ch]
    if (!g) {
      cursor += 4 * scale
      continue
    }
    for (let r = 0; r < 5; r++) {
      for (let c = 0; c < 3; c++) {
        if (g[r][c] === '1') fillRect(img, cursor + c * scale, y + r * scale, scale, scale, rgba)
      }
    }
    cursor += 4 * scale
  }
}

/** 选行列：优先等比（子格形状与父区域相近），否则尽量接近正方形。 */
function bestGrid(cells, w, h) {
  const parentAspect = w / Math.max(1, h)
  let best = Infinity
  let cols = 0
  let rows = 0
  for (let c = 1; c <= cells; c++) {
    if (cells % c) continue
    const r = cells / c
    const cellAspect = w / c / Math.max(1, h / r)
    const shapeErr = Math.abs(Math.log(cellAspect / parentAspect))
    const squareErr = Math.abs(Math.log(cellAspect))
    let score = shapeErr < 0.12 ? shapeErr : 1 + squareErr
    if (cellAspect > 3 || cellAspect < 1 / 3) score += 3
    if (score < best) {
      best = score
      cols = c
      rows = r
    }
  }
  if (cols && rows) return { cols, rows }
  cols = Math.max(1, Math.round(Math.sqrt(cells * parentAspect)))
  rows = Math.ceil(cells / cols)
  return { cols, rows }
}

function splitCells(region, cols, rows, limit) {
  const out = []
  for (let r = 0; r < rows && out.length < limit; r++) {
    for (let c = 0; c < cols && out.length < limit; c++) {
      const x = region.x + Math.round((region.w * c) / cols)
      const y = region.y + Math.round((region.h * r) / rows)
      const x2 = region.x + Math.round((region.w * (c + 1)) / cols)
      const y2 = region.y + Math.round((region.h * (r + 1)) / rows)
      out.push({ x, y, w: Math.max(1, x2 - x), h: Math.max(1, y2 - y) })
    }
  }
  return out
}

/** 最近邻放大（只放大，不插值 —— 放大后每个格子的坐标仍是原坐标）。 */
function scaleNearest(img, factor) {
  const w = Math.round(img.width * factor)
  const h = Math.round(img.height * factor)
  const data = Buffer.alloc(w * h * img.channels)
  for (let y = 0; y < h; y++) {
    const sy = Math.min(img.height - 1, Math.floor(y / factor))
    for (let x = 0; x < w; x++) {
      const sx = Math.min(img.width - 1, Math.floor(x / factor))
      const si = (sy * img.width + sx) * img.channels
      const di = (y * w + x) * img.channels
      for (let ch = 0; ch < img.channels; ch++) data[di + ch] = img.data[si + ch]
    }
  }
  return { width: w, height: h, channels: img.channels, data }
}

/** 把切分结果画成"每格带编号与坐标"的标注图（纯像素操作，不依赖字体）。 */
function renderCells(img, region, list, factor) {
  const canvas = scaleNearest(img, factor)
  const line = [255, 196, 0, 255]
  const ink = [255, 255, 255, 255]
  list.forEach((c, i) => {
    const lx = Math.round((c.x - region.x) * factor)
    const ly = Math.round((c.y - region.y) * factor)
    const lw = Math.round(c.w * factor)
    const lh = Math.round(c.h * factor)
    fillRect(canvas, lx, ly, lw, 2, line)
    fillRect(canvas, lx, ly + lh - 2, lw, 2, line)
    fillRect(canvas, lx, ly, 2, lh, line)
    fillRect(canvas, lx + lw - 2, ly, 2, lh, line)
    // 半透明底 + 编号（#i）；格子够大时再标左上角屏幕坐标
    fillRect(canvas, lx + 3, ly + 3, Math.min(lw - 6, 90), 20, [0, 0, 0, 190])
    drawText(canvas, lx + 5, ly + 6, `#${i + 1}`, 3, ink)
    if (lh > 46 && lw > 110) {
      drawText(canvas, lx + 5, ly + 30, `${c.x},${c.y}`, 2, ink)
    }
  })
  return canvas
}

async function cmdVision(argv) {
  const mode = arg(argv, '--mode', 'cells')
  if (mode !== 'cells') {
    emit({ ok: false, code: 'NOT_IMPLEMENTED', message: `macOS 引擎暂只支持 mode=cells（收到 ${mode}）` })
    return 4
  }
  const cells = intArg(argv, '--cells', 9)
  const outImage = arg(argv, '--out', join(process.cwd(), 'vision.png'))
  const key = arg(argv, '--window', null)
  const rectArg = arg(argv, '--rect', null)

  let region
  if (rectArg) {
    const [x, y, w, h] = rectArg.split(',').map((v) => parseInt(v, 10))
    if (![x, y, w, h].every((v) => Number.isFinite(v)) || w <= 0 || h <= 0) {
      emit({ ok: false, code: 'BAD_ARGS', message: '--rect 需要 x,y,w,h' })
      return 2
    }
    region = { x, y, w, h }
  } else if (key) {
    const win = pickWindow(await listWindows(), key)
    if (!win) {
      emit({ ok: false, code: 'WINDOW_NOT_FOUND', message: `window not found: ${key}` })
      return 3
    }
    region = { x: Math.round(win.x), y: Math.round(win.y), w: Math.round(win.w), h: Math.round(win.h) }
  } else {
    region = await screenRect()
  }

  // path 逐级下钻："7" → 第 7 格；"7.4" → 再下一次第 4 格
  const path = arg(argv, '--path', '')
  const crumbs = []
  if (path) {
    for (const seg of String(path).split('.').filter(Boolean)) {
      const pick = parseInt(seg, 10)
      const g = bestGrid(cells, region.w, region.h)
      const list = splitCells(region, g.cols, g.rows, cells)
      if (!Number.isFinite(pick) || pick < 1 || pick > list.length) {
        emit({ ok: false, code: 'BAD_ARGS', message: `path 段 ${seg} 超出 1..${list.length}` })
        return 2
      }
      region = list[pick - 1]
      crumbs.push(pick)
    }
  }

  const img = decodePng(await captureRect(region))
  const g = bestGrid(cells, region.w, region.h)
  const list = splitCells(region, g.cols, g.rows, cells)

  // 放大到长边约 1400（坐标不变，只影响可读性）
  const factor = Math.max(1, Math.min(6, 1400 / Math.max(img.width, img.height)))
  const canvas = renderCells(img, region, list, factor)

  mkdirSync(dirname(outImage), { recursive: true })
  writeFileSync(outImage, encodePng(canvas))

  emit({
    ok: true,
    mode: 'cells',
    image: outImage,
    region,
    path: crumbs.length ? crumbs.join('.') : '',
    cellsPerRow: g.cols,
    rows: g.rows,
    scale: Number(factor.toFixed(2)),
    cells: list.map((c, i) => ({
      index: i + 1,
      x: c.x,
      y: c.y,
      w: c.w,
      h: c.h,
      cx: Math.round(c.x + c.w / 2),
      cy: Math.round(c.y + c.h / 2),
    })),
    hint: `图上每格标了 #编号 与屏幕坐标；报告格子编号，或用 path:"${crumbs.concat('7').join('.')}" 只切那一格继续收窄。`,
    notes: ['engine=mac'],
  })
  return 0
}

function cmdUnsupported(cmd) {
  emit({ ok: false, code: 'NOT_IMPLEMENTED', message: `macOS 引擎（实验性）暂不支持 ${cmd}` })
  return 4
}

const USAGE = `engine-mac.mjs — Lens 的 macOS 引擎（实验性）

  node engine-mac.mjs info [--json] [--filter 关键词]
  node engine-mac.mjs scan --window <名称> [--json] [--overlay out.png]
  node engine-mac.mjs check --x X --y Y [--w 24] [--zoom 6] [--out png]
  node engine-mac.mjs click --x X --y Y [--button left]
  node engine-mac.mjs type --text "你好"
  node engine-mac.mjs key --combo "cmd+a"
  node engine-mac.mjs paste --text "多行文本"
  node engine-mac.mjs focus --window <进程名>
  node engine-mac.mjs window --action focus|minimize|restore|move|info --window <名称> [--x --y --w --h]
  node engine-mac.mjs cap [--window <名称>] --out png
  node engine-mac.mjs vision --mode cells [--cells 9] [--rect x,y,w,h | --window 名称] [--path 7.4] --out png
  node engine-mac.mjs hud            # 固定返回 HUD_UNAVAILABLE

未实现：scroll / drag / 遮挡检测，返回 NOT_IMPLEMENTED。
需要授权：系统设置 → 隐私与安全性 → 辅助功能、屏幕录制。`

async function main() {
  const [cmd, ...argv] = process.argv.slice(2)
  if (!cmd || cmd === '--help' || cmd === 'help') {
    console.log(USAGE)
    return 0
  }
  switch (cmd) {
    case 'info':
      return cmdInfo(argv)
    case 'scan':
      return cmdScan(argv)
    case 'check':
      return cmdCheck(argv)
    case 'click':
      return cmdClick(argv)
    case 'type':
      return cmdType(argv)
    case 'key':
      return cmdKey(argv)
    case 'paste':
      return cmdPaste(argv)
    case 'focus':
      return cmdFocus(argv)
    case 'window':
      return cmdWindow(argv)
    case 'cap':
      return cmdCap(argv)
    case 'vision':
      return cmdVision(argv)
    case 'hud':
      return cmdHud(argv)
    case 'scroll':
    case 'drag':
    case 'verify':
    case 'wait':
      return cmdUnsupported(cmd)
    default:
      console.log(USAGE)
      return 1
  }
}

// 只有被直接执行时才跑 CLI；被 import（测试 PNG 编解码）时不执行
const invokedDirectly = process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href
if (invokedDirectly) {
  main()
    .then((code) => process.exit(code || 0))
    .catch((e) => {
      emit({ ok: false, error: `${e && e.name}: ${e && e.message}` })
      process.exit(9)
    })
}

export { decodePng, encodePng, fillRect, strokeRect, pixelAt, drawText, bestGrid, splitCells, scaleNearest, renderCells }

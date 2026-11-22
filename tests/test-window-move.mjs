// 用真实窗口验证：move 之后回报的 frame 是否收敛到请求值（会移动窗口，结束后还原）
import { apply } from '../index.js'

const reg = new Map()
const ctx = {
  _d: [],
  effect(fn) { const d = fn(); if (typeof d === 'function') ctx._d.push(d) },
  tools: { register(t) { reg.set(t.name, t) }, guard() {} },
}
apply(ctx, { hudEnabled: false, timeoutMs: 20000, workDir: 'D:/code/computerconlex/_probe/tmp' })

// 目标窗口不写死：挑一个当前可见、足够大的普通窗口（记事本可能被用户关掉）
const wins = (await reg.get('computer_windows').execute({})).windows || []
const target = wins.find((w) => !w.minimized && !w.isHost && w.frame.w > 600 && w.frame.h > 400)
if (!target) {
  console.log('SKIP 没有可用窗口（全部最小化或没有权限）')
  process.exit(0)
}
const TITLE = target.title
console.log('# 使用窗口: ' + TITLE)
let fail = 0
const ok = (c, label, extra = '') => {
  console.log((c ? 'PASS ' : 'FAIL ') + label + (c ? '' : '  ' + extra))
  if (!c) fail++
}

const before = await reg.get('computer_window').execute({ window: TITLE, action: 'info' })
const orig = before.window?.frame || before.frame
ok(!!orig, '拿到 Notepad 原始几何', JSON.stringify(before).slice(0, 160))

const req = { window: TITLE, action: 'move', x: 200, y: 200, w: 900, h: 600 }
const moved = await reg.get('computer_window').execute(req)
const frame = moved.frame || moved.window?.frame
ok(!!frame, 'move 返回了 frame', JSON.stringify(moved).slice(0, 160))
if (frame) {
  ok(Math.abs(frame.x - 200) <= 3, 'x 与请求一致（±3）', `got ${frame.x}`)
  ok(Math.abs(frame.y - 200) <= 3, 'y 与请求一致（±3）', `got ${frame.y}`)
  ok(Math.abs(frame.w - 900) <= 3, 'w 与请求一致（±3）', `got ${frame.w}`)
  ok(Math.abs(frame.h - 600) <= 3, 'h 与请求一致（±3）', `got ${frame.h}`)
}
ok(typeof moved.corrections === 'number', '返回里带 corrections 次数', String(moved.corrections))
ok(!!moved.geometryNote, '返回里带几何说明')

// 还原
if (orig) {
  await reg.get('computer_window').execute({ window: TITLE, action: 'move', x: orig.x, y: orig.y, w: orig.w, h: orig.h })
}

// look/text 的坐标系：给屏幕坐标应当自动换算并能读到内容
const look = await reg.get('computer_look').execute({ window: TITLE, mode: 'text', rect: `${frame ? frame.x : 200},${frame ? frame.y + 40 : 240},600,120`, space: 'screen' })
ok(look.rectSpace === 'screen', 'text 模式识别出屏幕坐标系并换算', JSON.stringify(look).slice(0, 200))

for (const d of ctx._d) d()
process.exit(fail ? 1 : 0)

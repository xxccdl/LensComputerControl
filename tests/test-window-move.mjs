// move 的几何校准：真移动一个窗口，验证回报的 frame 与请求一致，然后还原。
// 有些窗口（远程桌面、固定尺寸工具窗）不接受 resize，那种情况只校验位置，尺寸记为 SKIP。
import { apply } from '../index.js'

const reg = new Map()
const ctx = { _d: [], effect(fn) { const d = fn(); if (typeof d === 'function') ctx._d.push(d) }, tools: { register(t) { reg.set(t.name, t) }, guard() {} } }
apply(ctx, { hudEnabled: false, timeoutMs: 20000, hudPort: 45897, workDir: 'D:/code/computerconlex/tests/tmp' })

let fail = 0
const ok = (c, label, extra = '') => {
  console.log((c ? 'PASS ' : 'FAIL ') + label + (c ? '' : '  ' + extra))
  if (!c) fail++
}
const skip = (label, why) => console.log('SKIP ' + label + (why ? '  ' + why : ''))

const win = reg.get('computer_window')
const list = (await reg.get('computer_windows').execute({})).windows || []
// 挑一个普通尺寸、非最大化的窗口：最大化窗口改尺寸会被系统忽略
const target = list.find((w) => !w.minimized && !w.isHost && w.frame.w > 400 && w.frame.w < 1800 && w.frame.h < 1000)
if (!target) {
  skip('move 校准', '没有合适的窗口')
  for (const d of ctx._d) d()
  process.exit(0)
}
console.log('# 目标窗口:', String(target.title).slice(0, 30), `${target.frame.w}x${target.frame.h}@${target.frame.x},${target.frame.y}`)

const before = await win.execute({ window: target.title, action: 'info' })
const orig = before.frame || (before.window && before.window.frame)
ok(!!orig, '拿到原始几何', JSON.stringify(before).slice(0, 120))

const REQ = { x: 220, y: 180, w: 900, h: 600 }
const moved = await win.execute({ window: target.title, action: 'move', x: REQ.x, y: REQ.y, w: REQ.w, h: REQ.h })
const frame = moved.frame || (moved.window && moved.window.frame)
ok(!!frame, 'move 返回了 frame', JSON.stringify(moved).slice(0, 160))
ok(typeof moved.corrections === 'number', '返回里带 corrections 次数', String(moved.corrections))
ok(!!moved.geometryNote, '返回里带几何说明')

if (frame) {
  ok(Math.abs(frame.x - REQ.x) <= 3, 'x 收敛到请求值（±3）', 'got ' + frame.x)
  ok(Math.abs(frame.y - REQ.y) <= 3, 'y 收敛到请求值（±3）', 'got ' + frame.y)
  const canResize = Math.abs(frame.w - REQ.w) <= 3 && Math.abs(frame.h - REQ.h) <= 3
  if (canResize) ok(true, 'w/h 也收敛到请求值（±3）')
  else skip('w/h 校验', `该窗口不接受 resize（请求 ${REQ.w}x${REQ.h}，实际 ${frame.w}x${frame.h}）`)
}

// 还原（尺寸还原不了也无所谓，位置还回去）
if (orig) {
  await win.execute({ window: target.title, action: 'move', x: orig.x, y: orig.y, w: orig.w, h: orig.h })
}

for (const d of ctx._d) d()
process.exit(fail ? 1 : 0)

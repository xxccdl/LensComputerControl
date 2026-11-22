// 可捕获性探测：probe 参数确实传到了引擎，并且回报了 capturable / captureMethod / blackRatio。
// 目标窗口不写死：挑当前可见窗口里第一个足够大的。
import { apply } from '../index.js'

const reg = new Map()
const ctx = { _d: [], effect(fn) { const d = fn(); if (typeof d === 'function') ctx._d.push(d) }, tools: { register(t) { reg.set(t.name, t) }, guard() {} } }
apply(ctx, { hudEnabled: false, timeoutMs: 20000, hudPort: 45896, workDir: 'D:/code/computerconlex/tests/tmp' })

let fail = 0
const ok = (c, label, extra = '') => {
  console.log((c ? 'PASS ' : 'FAIL ') + label + (c ? '' : '  ' + extra))
  if (!c) fail++
}

const windowsTool = reg.get('computer_windows')
const all = await windowsTool.execute({})
const target = (all.windows || []).find((w) => !w.minimized && !w.isHost && w.frame.w > 400 && w.frame.h > 300 && w.process)
if (!target) {
  console.log('SKIP 没有可用窗口')
  for (const d of ctx._d) d()
  process.exit(0)
}
const key = target.process

// 1) 带 probe：该窗口应当被补上 capturable 等字段
const probed = await windowsTool.execute({ probe: key })
const hit = (probed.windows || []).find((w) => w.process === key && w.capturable !== undefined)
ok(!!hit, `probe=${key} 给窗口补了 capturable 字段`, JSON.stringify((probed.windows || [])[0] || {}).slice(0, 160))
if (hit) {
  ok(typeof hit.capturable === 'boolean', 'capturable 是布尔值', String(hit.capturable))
  ok(typeof hit.captureMethod === 'string' && hit.captureMethod.length > 0, 'captureMethod 有值', String(hit.captureMethod))
  ok(typeof hit.blackRatio === 'number' && hit.blackRatio >= 0 && hit.blackRatio <= 1, 'blackRatio 在 0..1', String(hit.blackRatio))
}

// 2) 不带 probe：不应该有这些字段（避免每次列窗口都真抓一帧）
const plain = await windowsTool.execute({})
const noProbe = (plain.windows || []).find((w) => w.process === key)
ok(!noProbe || noProbe.capturable === undefined, '不带 probe 时不探测（性能）')

for (const d of ctx._d) d()
process.exit(fail ? 1 : 0)

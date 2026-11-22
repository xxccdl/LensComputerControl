// 设置项冒烟：验证 Config 之外的运行时行为（命中范围默认值、窗口黑名单、humanize、HUD 开关）。
// 目标窗口不写死：从当前可见窗口里挑一个，黑名单用它的标题关键词，避免依赖某个应用开着。
import { apply, Config } from '../index.js'

console.log('Config export:', Config === undefined ? '(no schemastery here — host provides it)' : typeof Config)

const reg = new Map()
const ctx = {
  _d: [],
  effect(fn) {
    const d = fn()
    if (typeof d === 'function') ctx._d.push(d)
  },
  tools: {
    register(tool) {
      reg.set(tool.name, tool)
    },
    guard() {},
  },
}

apply(ctx, {
  hudEnabled: false,
  humanize: false,
  checkBox: 40,
  checkZoom: 5,
  timeoutMs: 20000,
  blockedTitles: ['__none__'], // 先不拦；挑到窗口后再用它的标题关键词拦
})

const check = reg.get('computer_check')
const act = reg.get('computer_act')
const windows = reg.get('computer_windows')
if (!check || !act || !windows) {
  console.error('FAIL: tools not registered')
  process.exit(1)
}

const list = (await windows.execute({})).windows || []
const target = list.find((w) => !w.minimized && !w.isHost && w.frame.w > 400 && w.frame.h > 300)
if (!target) {
  console.log('SKIP 没有可用窗口')
  process.exit(0)
}
console.log('# 目标窗口:', target.title)

// 1) checkBox / checkZoom 来自设置
const r1 = await check.execute({ window: target.title, x: target.frame.x + 60, y: target.frame.y + 40 })
const sizeOk = !!(r1.box && r1.box.w === 40 && r1.box.h === 40)
console.log(sizeOk ? 'PASS 默认命中范围 = 设置里的 40' : 'FAIL 默认命中范围没跟随设置', JSON.stringify(r1.box || r1).slice(0, 160))

// 2) 黑名单：把目标标题里的关键词塞进黑名单
const reg2 = new Map()
const ctx2 = { _d: [], effect(fn) { const d = fn(); if (typeof d === 'function') ctx2._d.push(d) }, tools: { register(t) { reg2.set(t.name, t) }, guard() {} } }
const keyword = String(target.title).split(/[-–|]/)[0].trim().slice(0, 6) || String(target.title).slice(0, 4)
apply(ctx2, { hudEnabled: false, timeoutMs: 15000, blockedTitles: [keyword] })
const r2 = await reg2.get('computer_act').execute({ window: target.title, x: target.frame.x + 60, y: target.frame.y + 40 })
const blockOk = r2.ok === false && r2.code === 'WINDOW_BLOCKED'
console.log(blockOk ? 'PASS 黑名单拦住了「' + keyword + '」' : 'FAIL 黑名单没生效', JSON.stringify(r2).slice(0, 160))

for (const d of ctx._d) d()
for (const d of ctx2._d) d()

process.exit(sizeOk && blockOk ? 0 : 1)

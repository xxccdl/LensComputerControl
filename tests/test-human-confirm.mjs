// 人工确认开关：开了以后模型不能自行确认，HUD 那条路仍然可以
import { apply } from '../index.js'

const reg = new Map()
const ctx = { _d: [], effect(fn) { const d = fn(); if (typeof d === 'function') ctx._d.push(d) }, tools: { register(t) { reg.set(t.name, t) }, guard() {} } }
apply(ctx, { hudEnabled: false, timeoutMs: 20000, hudPort: 45895, confirmClicks: true, humanConfirm: true, workDir: 'D:/code/computerconlex/tests/tmp' })

let fail = 0
const ok = (c, label, extra = '') => {
  console.log((c ? 'PASS ' : 'FAIL ') + label + (c ? '' : '  ' + extra))
  if (!c) fail++
}
const act = reg.get('computer_act')
const wins = (await reg.get('computer_windows').execute({})).windows || []
const target = wins.find((w) => !w.minimized && !w.isHost && w.frame.w > 400 && w.frame.h > 300)
if (!target) {
  console.log('SKIP 没有可用窗口')
  process.exit(0)
}

// 1) 先拿一个预览令牌
const preview = await act.execute({ window: target.title, x: target.frame.x + 50, y: target.frame.y + 60 })
ok(preview && preview.token, '拿到预览令牌', JSON.stringify(preview).slice(0, 140))
const token = preview.token

// 2) 模型自己确认 → 必须被拒，且令牌还在（人还能点）
const byModel = await act.execute({ window: target.title, confirm: token })
ok(byModel.ok === false && byModel.code === 'HUMAN_CONFIRM_REQUIRED', '模型确认被拒（HUMAN_CONFIRM_REQUIRED）', JSON.stringify(byModel).slice(0, 200))
ok(!!(byModel.pending && byModel.pending.x !== undefined), '被拒时仍回 pending 信息（方便界面显示）')

// 3) 人的那条路（HTTP /confirm）→ 仍然可用
const base = 'http://127.0.0.1:45895'
const human = await (await fetch(base + '/confirm?token=' + encodeURIComponent(token))).json()
ok(human.code !== 'HUMAN_CONFIRM_REQUIRED', '人工确认不被该开关拦截', JSON.stringify(human).slice(0, 160))

for (const d of ctx._d) d()
process.exit(fail ? 1 : 0)

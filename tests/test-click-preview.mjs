// 坐标点击「预览 → 确认」流程测试。
// 预览阶段只跑 check（不点）；确认阶段真的发起一次点击 —— 目标选桌面（Program Manager）。
// 注意：DSH 窗口整屏盖在桌面上，引擎按设计会以 TARGET_COVERED 拒绝真点，
// 这里验的是「流程走通 + 点的是预览时记住的坐标 + 安全网仍生效」，不是"点成功了"。
import { existsSync } from 'node:fs'
import { apply } from '../index.js'

const reg = new Map()
const ctx = {
  _d: [],
  effect(fn) {
    const d = fn()
    if (typeof d === 'function') ctx._d.push(d)
  },
  tools: { register(t) { reg.set(t.name, t) }, guard() {} },
}

apply(ctx, { hudEnabled: false, timeoutMs: 30000 })
const act = reg.get('computer_act')
if (!act) {
  console.log('FAIL: computer_act 没注册')
  process.exit(1)
}

const win = 'Program Manager'
const px = 1600
const py = 900

// 1) 预览：不落点，回绿框图 + 令牌
const preview = await act.execute({ window: win, x: px, y: py })
const okPreview =
  preview && preview.ok === true && preview.clicked === false &&
  preview.code === 'CLICK_NEEDS_CONFIRM' && String(preview.token || '').startsWith('ck_') &&
  existsSync(preview.image)
console.log('preview:', JSON.stringify({
  ok: preview.ok, clicked: preview.clicked, code: preview.code,
  token: preview.token, box: preview.box,
}))
console.log(okPreview ? 'PASS 预览阶段没有点击，并给了令牌与绿框图' : 'FAIL 预览不符合预期')

// 2) 假令牌必须被拒
const bad = await act.execute({ window: win, confirm: 'ck_notexist' })
const okBad = bad && bad.ok === false && bad.code === 'CONFIRM_INVALID'
console.log('bad token:', bad && bad.code, '->', okBad ? 'PASS' : 'FAIL')

// 3) 真确认：用预览给的令牌落点（令牌记住预览时的坐标）
let okConfirm = false
if (okPreview) {
  const done = await act.execute({ window: win, confirm: preview.token })
  const safeRefusal = done && done.ok === false && (done.code === 'TARGET_COVERED' || done.code === 'POINT_OUTSIDE')
  okConfirm = !!done && (done.ok !== false || safeRefusal) && done.confirmed === true &&
    done.clickedAt && done.clickedAt.x === px && done.clickedAt.y === py
  console.log('confirm:', JSON.stringify({ ok: done.ok, code: done.code, confirmed: done.confirmed, clickedAt: done.clickedAt }))
  console.log(okConfirm ? 'PASS 确认走到真实点击（点在预览坐标；被遮挡时引擎仍拒绝）' : 'FAIL 确认流程不符合预期')

  // 4) 令牌一次性
  const again = await act.execute({ window: win, confirm: preview.token })
  const okOnce = again && again.ok === false && again.code === 'CONFIRM_INVALID'
  console.log('reuse token:', again && again.code, '->', okOnce ? 'PASS 令牌一次性' : 'FAIL 令牌可重放')
  okConfirm = okConfirm && okOnce
}

for (const d of ctx._d) d()
process.exit(okPreview && okBad && okConfirm ? 0 : 1)

// HUD 数据面端到端：预览 → GET /pending → GET /preview?token → POST /cancel，
// 再来一次 → POST /confirm（真点，桌面被 DSH 盖住时引擎会拒绝，属预期）。
import { apply } from '../index.js'

const PORT = 45899
const BASE = `http://127.0.0.1:${PORT}`
const reg = new Map()
const ctx = {
  _d: [],
  effect(fn) {
    const d = fn()
    if (typeof d === 'function') ctx._d.push(d)
  },
  tools: { register(t) { reg.set(t.name, t) }, guard() {} },
}

apply(ctx, { hudEnabled: false, hudPort: PORT, timeoutMs: 30000 })
await new Promise((r) => setTimeout(r, 400)) // 等 server.listen

const act = reg.get('computer_act')
const get = async (p) => (await fetch(BASE + p)).json()
let ok = true
const check = (label, pass) => { console.log((pass ? 'PASS ' : 'FAIL ') + label); ok = ok && pass }

const p1 = await act.execute({ window: 'Program Manager', x: 1500, y: 800 })
check('预览生成了令牌', p1 && p1.code === 'CLICK_NEEDS_CONFIRM' && !!p1.token)

const list = await get('/pending')
check('/pending 能看到这条待确认', Array.isArray(list.pending) && list.pending.some((p) => p.token === p1.token))
const item = (list.pending || []).find((p) => p.token === p1.token) || {}
check('/pending 带坐标与窗口', item.x === 1500 && item.y === 800 && item.window === 'Program Manager')

const img = await fetch(`${BASE}/preview?token=${p1.token}`)
check('/preview 返回 PNG', img.status === 200 && img.headers.get('content-type') === 'image/png')

const cancel = await get(`/cancel?token=${p1.token}`)
check('/cancel 取消成功', cancel.ok === true && cancel.cancelled === true)
const after = await get('/pending')
check('取消后列表里没了', !(after.pending || []).some((p) => p.token === p1.token))

const p2 = await act.execute({ window: 'Program Manager', x: 1500, y: 800 })
const confirmed = await get(`/confirm?token=${p2.token}`)
const touched = confirmed.confirmed === true && confirmed.clickedAt && confirmed.clickedAt.x === 1500
const safeRefusal = confirmed.ok === false && (confirmed.code === 'TARGET_COVERED' || confirmed.code === 'POINT_OUTSIDE')
check('/confirm 走到真实点击（被遮挡时引擎仍拒绝）', touched && (confirmed.ok !== false || safeRefusal))

const confirmAgain = await get(`/confirm?token=${p2.token}`)
check('令牌确认后不可复用', confirmAgain.ok === false && confirmAgain.code === 'CONFIRM_INVALID')

for (const d of ctx._d) d()
setTimeout(() => process.exit(ok ? 0 : 1), 200)

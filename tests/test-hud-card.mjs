// HUD「待确认点击」卡片渲染测试：直接渲染 PendingCard（纯函数组件，无 hooks）。
import { pathToFileURL } from 'node:url'

let captured = null
globalThis.window = {
  __ModuleLoader__: { load(s) { captured = s } },
  location: { reload() {} },
  localStorage: { getItem() { return null }, setItem() {}, removeItem() {} },
  innerWidth: 1920,
  innerHeight: 1080,
  addEventListener() {},
  removeEventListener() {},
}

const ReactStub = {
  createElement: (type, props, ...children) => ({ type, props: props || {}, children }),
  useState: (init) => [typeof init === 'function' ? init() : init, () => {}],
  useEffect() {},
  useRef: (v) => ({ current: v }),
  useMemo: (fn) => fn(),
  useCallback: (fn) => fn,
  useSyncExternalStore: (_s, get) => get(),
  Fragment: 'fragment',
}

await import(pathToFileURL('D:/code/computerconlex/client.js').href)
const client = captured.factory((n) => {
  if (n === 'react') return ReactStub
  throw new Error('unexpected require: ' + n)
})

const item = {
  token: 'ck_test1234', x: 412, y: 268, window: '微信',
  at: 1700000000000, covered: false, inside: true, candidate: { id: 17, text: '发送' },
}
const tree = client.internals.PendingCard({ item, more: 2, busy: false, onDecide: () => {} })
const flat = JSON.stringify(tree)

const checks = [
  ['卡片外壳', flat.includes('lensPending')],
  ['显示坐标', flat.includes('412') && flat.includes('268')],
  ['预览图指向 /preview?token=', flat.includes('/preview?token=ck_test1234')],
  ['确认按钮', flat.includes('确认点击')],
  ['取消按钮', flat.includes('取消')],
  ['多余条数提示', flat.includes('还有 2 条')],
]
let ok = true
for (const [label, pass] of checks) {
  console.log((pass ? 'PASS ' : 'FAIL ') + label)
  ok = ok && pass
}
if (!ok) console.log('\n渲染树：', flat.slice(0, 700))

// 按钮回调是否带对参数
const buttons = []
const walk = (n) => {
  if (!n || typeof n !== 'object') return
  if (n.type === 'button') buttons.push(n)
  for (const c of n.children || []) walk(c)
}
walk(tree)
let fired = null
const t2 = client.internals.PendingCard({ item, more: 0, busy: false, onDecide: (token, action) => { fired = token + ':' + action } })
const b2 = []
const walk2 = (n) => { if (!n || typeof n !== 'object') return; if (n.type === 'button') b2.push(n); for (const c of n.children || []) walk2(c) }
walk2(t2)
b2[0].props.onClick({ stopPropagation() {} })
const okConfirm = fired === 'ck_test1234:confirm'
fired = null
b2[1].props.onClick({ stopPropagation() {} })
const okCancel = fired === 'ck_test1234:cancel'
console.log((okConfirm ? 'PASS ' : 'FAIL ') + '确认按钮回调带令牌')
console.log((okCancel ? 'PASS ' : 'FAIL ') + '取消按钮回调带令牌')
process.exit(ok && okConfirm && okCancel ? 0 : 1)

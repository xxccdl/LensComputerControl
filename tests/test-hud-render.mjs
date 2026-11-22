// HUD 渲染测试：用最小 hooks 运行时真跑一次 Hud()，让 /status 返回一条 pending，
// 验证待确认卡片（绿框预览图 + 确认/取消）确实出现在渲染树里。
// 断言尽量用类名与 token（ASCII），避免中文字面量在脚本改写中被打乱。
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
globalThis.setInterval = () => 0
globalThis.clearInterval = () => {}

const slots = []
const effectSlots = []
let cursor = 0
let dirty = false

const ReactStub = {
  createElement: (type, props, ...children) => ({ type, props: props || {}, children }),
  Fragment: 'fragment',
  useState(init) {
    const i = cursor++
    if (!(i in slots)) slots[i] = typeof init === 'function' ? init() : init
    return [slots[i], (next) => {
      slots[i] = typeof next === 'function' ? next(slots[i]) : next
      dirty = true
    }]
  },
  useEffect(fn, deps) {
    const i = cursor++
    const prev = effectSlots[i]
    const changed = !prev || !deps || !prev.deps || deps.some((d, j) => d !== prev.deps[j])
    effectSlots[i] = { deps, changed: prev ? changed : true, fn }
  },
  useLayoutEffect() { cursor++ },
  useRef(v) { const i = cursor++; if (!(i in slots)) slots[i] = { current: v }; return slots[i] },
  useMemo(fn) { cursor++; return fn() },
  useCallback(fn) { cursor++; return fn },
  useSyncExternalStore(_sub, get) { const i = cursor++; slots[i] = get(); return slots[i] },
}

const pendingPayload = {
  ok: true,
  stopped: false,
  uptimeMs: 1000,
  lastScan: null,
  timeline: [{ at: Date.now(), kind: 'scan', detail: 'WX 23', ok: true }],
  platform: 'win32',
  engine: 'exe',
  engineLabel: 'Windows engine (lens.exe)',
  hudMode: 'native',
  permission: null,
  pending: [{
    token: 'ck_test1234', x: 412, y: 268, window: 'WX',
    at: Date.now(), covered: false, inside: true, candidate: { id: 17, text: 'SEND' },
  }],
}
globalThis.fetch = async () => ({ json: async () => pendingPayload })

await import(pathToFileURL('D:/code/computerconlex/client.js').href)
const client = captured.factory((n) => {
  if (n === 'react') return ReactStub
  throw new Error('unexpected require: ' + n)
})

const registered = []
const ctx = {
  effect(fn) { fn() },
  slots: { inject(_n, fn) { fn() }, register(spec, Comp) { registered.push({ spec, Comp }); return spec } },
  inject(_d, fn) {
    fn({ configForms: { get: () => null, describe: () => ({ subscribe: () => () => {}, getSnapshot: () => ({ view: { namespaces: [] } }), ensure() {}, load: async () => {} }) } })
  },
}

client.apply(ctx)
const hud = registered.find((r) => r.spec.id === 'lens-hud')

function runEffects() {
  for (const slot of effectSlots) {
    if (slot && slot.changed) {
      slot.changed = false
      try { slot.fn() } catch { /* 测试里忽略 */ }
    }
  }
}

let tree
for (let i = 0; i < 8; i++) {
  cursor = 0
  dirty = false
  tree = hud.Comp({})
  runEffects()
  await new Promise((r) => setTimeout(r, 0))
  await new Promise((r) => setTimeout(r, 0))
  if (!dirty) break
}

// 只统计「元素」里的类名与按钮，不把 <style> 里的 CSS 文本算进来。
// 函数组件要展开：桩里 h(Component, props) 只是记录，不像 React 会去调用它。
const nodes = []
const texts = []
const walk = (n, depth = 0) => {
  if (!n || typeof n !== 'object' || depth > 20) return
  if (Array.isArray(n)) { n.forEach((c) => walk(c, depth + 1)); return }
  if (n.type === 'style') return
  if (typeof n.type === 'function') {
    walk(n.type(n.props || {}), depth + 1)
    return
  }
  if (n.props && n.props.className) nodes.push(String(n.props.className))
  if (n.props && typeof n.props.src === 'string') texts.push(n.props.src)
  if (n.type === 'button') nodes.push('BUTTON:' + (n.children || []).map(String).join(''))
  for (const k of n.children || []) { if (typeof k === 'string') texts.push(k); walk(k, depth + 1) }
}
walk(tree)

const has = (cls) => nodes.some((s) => s.split(/\s+/).includes(cls))
const json = JSON.stringify(tree)
const flatText = texts.join(' | ')
let fail = 0
const ok = (c, label, extra = '') => {
  console.log((c ? 'PASS ' : 'FAIL ') + label + (c ? '' : '  ' + extra))
  if (!c) fail++
}

ok(has('lensPending'), '待确认卡片出现在树上（lensPending 元素，非 CSS）')
ok(has('lensPendingBtns'), '卡片里有按钮容器')
const buttons = nodes.filter((s) => s.startsWith('BUTTON:'))
ok(buttons.length >= 2, '至少两个按钮', String(buttons.length))
ok(flatText.includes('/preview?token=ck_test1234'), '预览图指向 /preview?token=…')
ok(flatText.includes('412') && flatText.includes('268'), '卡片带坐标 412,268')
ok(flatText.includes('#17'), '命中候选 chip 显示 #17')
ok(has('lensOk'), '动作流的状态点存在')
ok(has('lensStop'), '底部急停按钮存在')
ok(json.includes('READY') || json.includes('L1') || json.includes('L2'), '头部有状态胶囊')

if (fail) {
  console.log('\n节点类名：', nodes.slice(0, 24).join(' | '))
  console.log('按钮：', buttons.join(' | '))
}
process.exit(fail ? 1 : 0)

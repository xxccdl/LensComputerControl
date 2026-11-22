// 客户端冒烟：假 window.__ModuleLoader__ + 假 React 加载 client.js，
// 1) 确认 apply() 往 settings.section 注册「Lens 电脑控制」；
// 2) 用假 configForms（真实形状：describe 是可订阅的 mirror）渲染面板，确认走"有表单"那条路。
import { pathToFileURL } from 'node:url'

let captured = null
globalThis.window = {
  __ModuleLoader__: { load(spec) { captured = spec } },
  location: { reload() {} },
}

const ReactStub = {
  createElement: (type, props, ...children) => ({ type, props, children }),
  useState: (init) => [typeof init === 'function' ? init() : init, () => {}],
  useEffect: () => {},
  useRef: (v) => ({ current: v }),
  useMemo: (fn) => fn(),
  useCallback: (fn) => fn,
  useSyncExternalStore: (_sub, getSnap) => getSnap(),
  Fragment: 'fragment',
}

await import(pathToFileURL('D:/code/computerconlex/client.js').href)
if (!captured) {
  console.error('FAIL: client.js 没有调用 __ModuleLoader__.load')
  process.exit(1)
}
const client = captured.factory((name) => {
  if (name === 'react') return ReactStub
  throw new Error('unexpected require: ' + name)
})

const fakeScope = {
  subscribe: () => () => {},
  getSnapshot: () => ({ status: 'ready', writable: true, revision: 3, value: { hudEnabled: true, humanize: false, checkBox: 32 } }),
  mutate: async () => true,
}

const fakeMirror = {
  subscribe: () => () => {},
  getSnapshot: () => ({ view: { writable: true, namespaces: [{ ns: 'lens-computer-control', revision: 3, value: {} }] } }),
  ensure: () => {},
  load: async () => {},
}

const registered = []
const ctx = {
  effect(fn) { fn() },
  slots: {
    inject(_name, fn) { fn() },
    register(spec, Comp) { registered.push(Object.assign({ Comp }, spec)); return spec },
  },
  inject(_deps, fn) {
    fn({ configForms: { get: (ns) => (ns === 'lens-computer-control' ? fakeScope : undefined), describe: () => fakeMirror } })
  },
}

client.apply(ctx)
console.log('registered:', registered.map((s) => `${s.name}#${s.id}`).join(', '))
const section = registered.find((s) => s.name === 'settings.section')
if (!section) {
  console.log('FAIL: 没注册 settings.section')
  process.exit(1)
}

const tree = section.Comp({ mirror: fakeMirror, pick: (ns) => (ns === 'lens-computer-control' ? fakeScope : null) })
const flat = JSON.stringify(tree)
console.log('render bytes:', flat.length)
const checks = [
  ['标题', flat.includes('Lens 电脑控制')],
  ['人类化输入开关', flat.includes('人类化输入节奏')],
  ['坐标检验段', flat.includes('坐标检验')],
  ['窗口黑名单', flat.includes('永不操作的窗口')],
  ['没有走「暂无配置」分支', !flat.includes('还没有为这个插件提供配置项')],
]
let ok = true
for (const [label, pass] of checks) {
  console.log((pass ? 'PASS ' : 'FAIL ') + label)
  ok = ok && pass
}
process.exit(ok ? 0 : 1)

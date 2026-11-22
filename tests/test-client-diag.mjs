// 设置面板的诊断块：不引真 React，用桩把元素树取出来断言（与 test-hud-card 同风格）
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
globalThis.fetch = async () => ({ json: async () => ({ ok: true }) })

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

const req = (name) => (name === 'react' ? ReactStub : {})

await import('../client.js')
if (!captured) {
  console.error('FAIL: client.js 没有通过 __ModuleLoader__ 注册')
  process.exit(1)
}
const mod = captured.factory(req)
const { LensDiag } = mod.internals || {}

let fail = 0
const ok = (c, label, extra = '') => {
  console.log((c ? 'PASS ' : 'FAIL ') + label + (c ? '' : '  ' + extra))
  if (!c) fail++
}

ok(typeof LensDiag === 'function', 'internals 里导出了 LensDiag')

// 把元素树拍平成字符串，找关键文案与类名
const flatten = (node, out = []) => {
  if (node == null || node === false) return out
  if (Array.isArray(node)) {
    for (const n of node) flatten(n, out)
    return out
  }
  if (typeof node === 'string' || typeof node === 'number') {
    out.push(String(node))
    return out
  }
  if (node.props) {
    if (node.props.className) out.push('.' + node.props.className)
    const kids = node.children || []
    for (const k of kids) flatten(k, out)
  }
  return out
}

const flat = flatten(LensDiag({})).join(' | ')
ok(flat.includes('.lensDiag'), '渲染出诊断块外壳', flat.slice(0, 80))
ok(flat.includes('引擎诊断'), '标题「引擎诊断」')
ok(flat.includes('重新检测'), '有「重新检测」按钮')
ok(flat.includes('正在读取'), '首屏占位是「正在读取…」')
ok(!flat.includes('undefined'), '没有把 undefined 渲染出来')
ok(!flat.includes('NaN'), '没有把 NaN 渲染出来')

// 有 info 时应当逐行输出；这里直接验渲染分支存在（状态由 useState 桩控制，取初值 null）
ok(flat.includes('.lensDiagHead'), '头部类名存在')

console.log(fail ? `\nFAILED ${fail}` : '\nALL PASS')
process.exit(fail ? 1 : 0)

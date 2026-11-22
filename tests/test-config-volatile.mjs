// 用宿主自带的 schemastery 验证插件真实的 Config：
// 每个字段是否都带 volatile（宿主只把 volatile 字段暴露到设置命名空间）、默认值、校验是否通过。
import { createRequire } from 'node:module'
import { registerHooks } from 'node:module'
import { pathToFileURL } from 'node:url'

let fail = 0
const ok = (c, label, extra = '') => {
  console.log((c ? 'PASS ' : 'FAIL ') + label + (c ? '' : '  ' + extra))
  if (!c) fail++
}

// 先把 @deepseek-ai/* 的解析兜底到宿主二进制，再 import 插件
const hostEntry = 'D:/dshdesktop/DSH Desktop/resources/app.asar/node_modules/@deepseek-ai/dsh/lib/bin.js'
const hostEntryUrl = pathToFileURL(hostEntry).href
const RETRYABLE = new Set(['ERR_MODULE_NOT_FOUND', 'ERR_PACKAGE_PATH_NOT_EXPORTED'])
registerHooks({
  resolve(specifier, context, nextResolve) {
    try {
      return nextResolve(specifier, context)
    } catch (error) {
      if (!specifier.startsWith('@deepseek-ai/') || !RETRYABLE.has(error?.code)) throw error
      return nextResolve(specifier, { ...context, parentURL: hostEntryUrl })
    }
  },
})

const require = createRequire('D:/dshdesktop/DSH Desktop/resources/app/package.json')
const zmod = require('@deepseek-ai/schemastery')
const z = zmod.default ?? zmod
ok(typeof z.object === 'function', '拿到宿主的 schemastery')

const mod = await import(pathToFileURL('D:/code/computerconlex/index.js').href)
const Config = mod.Config
ok(!!Config, '插件导出了 Config', String(Config))

const dict = (Config && (Config.dict || Config._properties)) || {}
const marks = Object.entries(dict).map(([k, v]) => ({ field: k, volatile: !!(v.meta && v.meta.volatile) }))
ok(marks.length >= 8, '字段数量正常（>=8）', String(marks.length))
const stillStatic = marks.filter((m) => !m.volatile).map((m) => m.field)
ok(stillStatic.length === 0, '所有字段都是 volatile（否则设置页看不到）', stillStatic.join(','))

const r = Config['~standard'].validate({ timeoutMs: 30000, blockedTitles: ['网银'] })
ok(r && !r.issues, '校验通过', String(JSON.stringify((r && r.issues) || '')).slice(0, 160))
const value = (r && r.value) || {}
ok(value.timeoutMs !== undefined, '值里有 timeoutMs', Object.keys(value).join(','))

// volatile 字段是"活引用"：读的时候才求值（宿主改配置不用重载）
const live = typeof value.timeoutMs === 'object' && value.timeoutMs && typeof value.timeoutMs.get === 'function'
ok(live || typeof value.timeoutMs === 'number', 'timeoutMs 是可读的（活引用或数值）', typeof value.timeoutMs)
if (live) ok(value.timeoutMs.get() === 30000, '活引用读到 30000', String(value.timeoutMs.get()))

const bad = Config['~standard'].validate({ timeoutMs: 1 })
ok(!bad || !!bad.issues, '超范围的值被拒绝（timeoutMs 下限 2000）')

process.exit(fail ? 1 : 0)

// 模拟宿主的模块兜底（resources/host-module-fallback.mjs），看插件里的
// `await import('@deepseek-ai/schemastery')` 在宿主环境下到底能不能解析。
import { registerHooks } from 'node:module'
import { pathToFileURL } from 'node:url'

const hostEntry = 'D:/dshdesktop/DSH Desktop/resources/app.asar/node_modules/@deepseek-ai/dsh/lib/bin.js'
const hostEntryUrl = pathToFileURL(hostEntry).href
const RETRYABLE = new Set(['ERR_MODULE_NOT_FOUND', 'ERR_PACKAGE_PATH_NOT_EXPORTED'])

let fail = 0
const ok = (c, label, extra = '') => {
  console.log((c ? 'PASS ' : 'FAIL ') + label + (c ? '' : '  ' + extra))
  if (!c) fail++
}

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

const mod = await import(pathToFileURL('D:/code/computerconlex/index.js').href)
ok(typeof mod.apply === 'function', 'index.js 导出了 apply')
ok(mod.Config !== undefined, 'Config 在宿主兜底环境下可解析（设置表单靠它）', String(mod.Config))

if (mod.Config) {
  const r = mod.Config['~standard'].validate({})
  ok(r && !r.issues, 'Config 用默认值能通过校验', String(JSON.stringify((r && r.issues) || '')).slice(0, 160))
  const value = (r && r.value) || {}
  ok('timeoutMs' in value && 'checkBox' in value, '默认值里有 timeoutMs / checkBox', Object.keys(value).join(','))
}

process.exit(fail ? 1 : 0)

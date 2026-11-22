// 脚本解析器 + run 的离线行为（不依赖 macOS：只测解析与"未知动词"这类纯逻辑）
import { parseScriptLine } from '../engine-mac.mjs'

let fail = 0
const ok = (c, label, extra = '') => {
  console.log((c ? 'PASS ' : 'FAIL ') + label + (c ? '' : '  ' + extra))
  if (!c) fail++
}

const a = parseScriptLine('key combo=ctrl+a')
ok(a.verb === 'key' && a.kv.combo === 'ctrl+a', '解析 key combo=', JSON.stringify(a))

const b = parseScriptLine('type text="你好 世界"')
ok(b.verb === 'type' && b.kv.text === '你好 世界', '带引号的值保留空格', JSON.stringify(b))

const c = parseScriptLine('window 微信')
ok(c.verb === 'window' && c.bare.join(' ') === '微信', 'window 后的裸词是标题', JSON.stringify(c))

const d = parseScriptLine('click find=text:确定')
ok(d.verb === 'click' && d.kv.find === 'text:确定', 'find=text:… 解析', JSON.stringify(d))

const e = parseScriptLine('click id=6 count=2')
ok(e.kv.id === '6' && e.kv.count === '2', '多组 key=value', JSON.stringify(e))

const f = parseScriptLine('scroll x=500 y=400 delta=-3 times=2')
ok(f.kv.delta === '-3' && f.kv.x === '500' && f.kv.times === '2', '负数与多参数', JSON.stringify(f))

const g = parseScriptLine('   ')
ok(g.verb === '', '空行解析成空动词（外层会跳过）', JSON.stringify(g))

const h = parseScriptLine('KEY COMBO=Enter')
ok(h.verb === 'key' && h.kv.combo === 'Enter', '动词与键名大小写不敏感', JSON.stringify(h))

const i = parseScriptLine("paste text='单引号 也可以'")
ok(i.kv.text === '单引号 也可以', '单引号值', JSON.stringify(i))

console.log(fail ? `\nFAILED ${fail}` : '\nALL PASS')
process.exit(fail ? 1 : 0)

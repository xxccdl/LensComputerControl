// 新测试：覆盖这次实测报的 P0 里可以离线验证的部分
import { parseCombo } from '../engine-mac.mjs'
import { toJsonSafeForTest } from '../index.js'

let fail = 0
const ok = (c, label, extra = '') => {
  console.log((c ? 'PASS ' : 'FAIL ') + label + (c ? '' : '  ' + extra))
  if (!c) fail++
}

// ── P0-8：组合键解析（原来 se.keystroke(key,{using}) 直接 -1700）
const cmdA = parseCombo('cmd+a')
ok(cmdA && cmdA.code === 0 && cmdA.flags === 1 << 20, 'cmd+a 解析成 keycode 0 + command', JSON.stringify(cmdA))
const ctrlShiftA = parseCombo('ctrl+shift+a')
ok(ctrlShiftA && ctrlShiftA.flags === (1 << 18) | (1 << 17), 'ctrl+shift 两个修饰键叠加', JSON.stringify(ctrlShiftA))
ok(parseCombo('enter') && parseCombo('enter').code === 36, 'enter → 36')
ok(parseCombo('escape').code === 53 && parseCombo('esc').code === 53, 'escape/esc 同一键')
ok(parseCombo('cmd+') === null || parseCombo('') === null, '空组合返回 null（上层报 KEY_UNKNOWN）')
ok(parseCombo('cmd+不认识的键') === null, '不认识的键返回 null', JSON.stringify(parseCombo('cmd+不认识的键')))

// ── P0-3 / P0-4：可序列化（Buffer/NaN/undefined/函数都要清掉）
const safe = toJsonSafeForTest({
  ok: true,
  buf: Buffer.from('x'),
  nan: NaN,
  inf: Infinity,
  undef: undefined,
  fn: () => 1,
  nested: { a: [1, undefined, NaN], b: { c: undefined } },
})
const round = JSON.stringify(safe)
ok(safe.buf === '[buffer 1B]', 'Buffer 变成长度字符串', String(safe.buf))
ok(safe.nan === null && safe.inf === null, 'NaN/Infinity → null', `${safe.nan}/${safe.inf}`)
ok(!('undef' in safe), 'undefined 的键被删掉')
ok(!('fn' in safe), '函数被删掉')
ok(round.includes('"a":[1,null,null]'), '数组里的空洞变 null', round)
ok(!('c' in safe.nested.b), '深层 undefined 也删掉')
ok(JSON.parse(round), '结果能 JSON.parse（lossless）')

process.exit(fail ? 1 : 0)

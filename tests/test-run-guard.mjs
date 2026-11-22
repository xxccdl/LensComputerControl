import { apply } from '../index.js'
const reg = new Map()
const ctx = { _d: [], effect(fn){const d=fn(); if(typeof d==='function')ctx._d.push(d)}, tools:{register(t){reg.set(t.name,t)},guard(){}} }
apply(ctx, { hudEnabled:false, timeoutMs:20000 })
const run = reg.get('computer_run')
let ok = true
const chk = (l,p)=>{ console.log((p?'PASS ':'FAIL ')+l); ok = ok && p }
const r1 = await run.execute({ script: 'window WPS\nfocus\nclick x=245 y=25' })
chk('脚本兜底：坐标点击被拦', r1.ok === false && r1.code === 'SCRIPT_COORD_CLICK_BLOCKED' && (r1.offending||[]).length === 1)
const r2 = await run.execute({ script: 'window WPS\nclick find=text:稻壳' })
chk('脚本里按候选定位不受影响（不报 SCRIPT_COORD_CLICK_BLOCKED）', !(r2 && r2.code === 'SCRIPT_COORD_CLICK_BLOCKED'))
const r3 = await run.execute({ script: 'window WPS\nclick x=245 y=25', force: true })
chk('force:true 可放行', !(r3 && r3.code === 'SCRIPT_COORD_CLICK_BLOCKED'))
for (const d of ctx._d) d()
process.exit(ok?0:1)

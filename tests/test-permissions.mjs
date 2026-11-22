import { apply } from '../index.js'
const reg = new Map()
const ctx = { _d: [], effect(fn){const d=fn(); if(typeof d==='function') ctx._d.push(d)}, tools:{register(t){reg.set(t.name,t)}, guard(){}} }
apply(ctx, { hudEnabled:false, timeoutMs:15000, hudPort: 45899, workDir:'D:/code/computerconlex/tests/tmp' })
// 打 /status 与 /permissions 两个口，确认 Windows 上行为正常（不需要 mac）
const base = 'http://127.0.0.1:45899'
const s = await (await fetch(base + '/status')).json()
console.log((s.platform ? 'PASS' : 'FAIL') + ' /status 返回 platform=' + s.platform + ' engine=' + s.engine)
console.log((s.permissions === null || typeof s.permissions === 'object' ? 'PASS' : 'FAIL') + ' /status 带 permissions 字段: ' + JSON.stringify(s.permissions))
const g = await (await fetch(base + '/permissions')).json()
console.log((typeof g === 'object' ? 'PASS' : 'FAIL') + ' GET /permissions -> ' + JSON.stringify(g).slice(0,120))
const p = await (await fetch(base + '/permissions', { method:'POST' })).json()
console.log((p.ok === true ? 'PASS' : 'FAIL') + ' POST /permissions -> ' + JSON.stringify(p).slice(0,140))
for (const d of ctx._d) d()

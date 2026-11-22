import { apply } from '../index.js'
const reg = new Map()
const ctx = { _d: [], effect(fn){const d=fn(); if(typeof d==='function') ctx._d.push(d)}, tools:{register(t){reg.set(t.name,t)}, guard(){}} }
apply(ctx, { hudEnabled:false, timeoutMs:20000, workDir:'D:/code/computerconlex/_probe/tmp' })
const r = await reg.get('computer_windows').execute({ filter: 'Notepad' })
console.log((r.ok ? 'PASS' : 'FAIL') + ' windows 工具解析了带 BOM 的引擎输出；count=' + r.count)
for (const d of ctx._d) d()

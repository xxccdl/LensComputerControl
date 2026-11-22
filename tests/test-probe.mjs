import { apply } from '../index.js'
const reg = new Map()
const ctx = { _d: [], effect(fn){const d=fn(); if(typeof d==='function') ctx._d.push(d)}, tools:{register(t){reg.set(t.name,t)}, guard(){}} }
apply(ctx, { hudEnabled:false, timeoutMs:20000, workDir:'D:/code/computerconlex/_probe/tmp' })
const r = await reg.get('computer_windows').execute({ filter: '下载', probe: '下载' })
const w = (r.windows || [])[0]
console.log((w && w.capturable === true ? 'PASS' : 'FAIL') + ' probe 参数生效，capturable=' + (w && w.capturable) + ' method=' + (w && w.captureMethod) + ' black=' + (w && w.blackRatio))
for (const d of ctx._d) d()

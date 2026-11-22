// 平台选择：darwin 走 node + engine-mac.mjs，win32 走 bin/lens.exe
import { enginePlan } from '../index.js'

let fail = 0
const ok = (cond, label, extra = '') => {
  console.log((cond ? 'PASS ' : 'FAIL ') + label + (cond ? '' : '  ' + extra))
  if (!cond) fail++
}

const here = 'D:/code/computerconlex'
const mac = enginePlan('darwin', here, undefined)
ok(mac.engine === 'node', 'darwin 用 node 引擎', mac.engine)
ok(mac.pre.length === 1 && /engine-mac\.mjs$/.test(mac.pre[0]), 'darwin 传入 engine-mac.mjs', JSON.stringify(mac.pre))
ok(mac.env && mac.env.ELECTRON_RUN_AS_NODE === '1', 'darwin 设置 ELECTRON_RUN_AS_NODE（Electron 下当 node 用）')
ok(/node|DSH Desktop\.exe$/i.test(mac.cmd) || mac.cmd.includes('node'), 'darwin 用当前可执行文件跑脚本', mac.cmd)

const win = enginePlan('win32', here, undefined)
ok(win.engine === 'exe', 'win32 用 exe 引擎', win.engine)
ok(win.pre.length === 0, 'win32 不需要前置参数')
ok(/bin[\\/]lens\.exe$/.test(win.cmd), 'win32 指向 bin/lens.exe', win.cmd)
ok(win.env === undefined, 'win32 不改环境')

const custom = enginePlan('win32', here, 'D:/tmp/other.exe')
ok(custom.cmd === 'D:/tmp/other.exe', 'win32 尊重 cfg.exePath 覆盖', custom.cmd)

const linux = enginePlan('linux', here, undefined)
ok(linux.engine === 'exe', '其它平台仍按 exe 处理（保持原行为，不乱猜）', linux.engine)

process.exit(fail ? 1 : 0)

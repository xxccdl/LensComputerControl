import { readFileSync, existsSync, rmSync } from 'node:fs'
import { join } from 'node:path'
import { execFileSync } from 'node:child_process'
import { tmpdir } from 'node:os'

let fail = 0
const ok = (c, label, extra = '') => {
  console.log((c ? 'PASS ' : 'FAIL ') + label + (c ? '' : '  ' + extra))
  if (!c) fail++
}

const statePath = join(tmpdir(), 'lens-hud-mac.json')
try { rmSync(statePath) } catch {}

const run = (args) => {
  try {
    return JSON.parse(execFileSync(process.execPath, ['engine-mac.mjs', ...args], { encoding: 'utf8' }))
  } catch (e) {
    return { ok: false, raw: String(e.stdout || e.message).slice(0, 200) }
  }
}

// pulse 应当写状态文件（Windows 上也算，因为只是写 JSON）
const p1 = run(['hud', '--action', 'pulse', '--text', 'scan 微信 23 候选', '--kind', 'scan'])
ok(p1.ok === true, 'pulse 返回 ok', JSON.stringify(p1).slice(0, 120))
ok(existsSync(statePath), 'pulse 写出了状态文件', statePath)
const s1 = JSON.parse(readFileSync(statePath, 'utf8'))
ok(typeof s1.seq === 'number' && s1.seq >= 1, '状态里有递增 seq', String(s1.seq))
ok(s1.text === 'scan 微信 23 候选', '文本写进去了', String(s1.text))
ok(s1.kind === 'scan', 'kind 写进去了', String(s1.kind))
ok(s1.hide === false, 'pulse 时 hide=false')

// 再 pulse 一次：seq 必须递增（浮层据此判断"有新动作"）
const p2 = run(['hud', '--action', 'pulse', '--text', 'click 412,268'])
const s2 = JSON.parse(readFileSync(statePath, 'utf8'))
ok(s2.seq > s1.seq, 'seq 递增', `${s1.seq} → ${s2.seq}`)

// hide：浮层应当隐藏
run(['hud', '--action', 'hide'])
const s3 = JSON.parse(readFileSync(statePath, 'utf8'))
ok(s3.hide === true, 'hide 写 hide=true')

// stop：同时要求进程退出
run(['hud', '--action', 'stop'])
const s4 = JSON.parse(readFileSync(statePath, 'utf8'))
ok(s4.quit === true && s4.hide === true, 'stop 同时置 quit=true', JSON.stringify(s4))

// status：Windows 上没有 swiftc，应明确报 buildError.reason 而不是假装成功
const st = run(['hud', '--action', 'status'])
ok(st.ok === true && st.running === false, 'status 返回 running=false')
ok(st.binary === null && st.buildError && st.buildError.reason, 'status 报出编译不可用的原因', JSON.stringify(st.buildError).slice(0, 160))

try { rmSync(statePath) } catch {}
process.exit(fail ? 1 : 0)

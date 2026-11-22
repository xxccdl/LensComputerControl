#!/usr/bin/env node
/**
 * 一把跑完所有本地测试：node tools/test-all.mjs [--filter 关键词]
 *
 * 测试都在 tests/test-*.mjs 里，各自独立进程运行（有的会真去操作窗口，
 * 崩了不该带崩别人）。断言输出约定：以 PASS / FAIL / SKIP 开头的行。
 */
import { readdirSync } from 'node:fs'
import { join, dirname } from 'node:path'
import { spawnSync } from 'node:child_process'
import { fileURLToPath } from 'node:url'

const here = dirname(fileURLToPath(import.meta.url))
const probeDir = join(here, '..', 'tests')
const filterArg = process.argv.indexOf('--filter')
const filter = filterArg > 0 ? String(process.argv[filterArg + 1] || '') : ''

const files = readdirSync(probeDir)
  .filter((f) => /^test-.*\.mjs$/.test(f))
  .filter((f) => !filter || f.includes(filter))
  .sort()

if (!files.length) {
  console.error('没有匹配的测试文件' + (filter ? `（filter=${filter}）` : ''))
  process.exit(1)
}

const rows = []
let totalPass = 0
let totalFail = 0
let totalSkip = 0

for (const file of files) {
  const started = Date.now()
  const res = spawnSync(process.execPath, [join(probeDir, file)], {
    encoding: 'utf8',
    timeout: 120000,
    cwd: join(here, '..'),
  })
  const out = String(res.stdout || '') + String(res.stderr || '')
  const pass = (out.match(/^PASS /gm) || []).length
  const fail = (out.match(/^FAIL /gm) || []).length
  const skip = (out.match(/^SKIP /gm) || []).length
  totalPass += pass
  totalFail += fail
  totalSkip += skip
  rows.push({
    file,
    pass,
    fail,
    skip,
    ms: Date.now() - started,
    crashed: res.status !== 0 && fail === 0,
    status: res.status,
    detail: fail > 0 || (res.status !== 0 && fail === 0) ? out.split('\n').filter((l) => /^(FAIL|Error|.*Error:)/.test(l)).slice(0, 3) : [],
  })
}

const width = Math.max(...rows.map((r) => r.file.length), 10)
console.log('\n' + 'file'.padEnd(width) + '  PASS  FAIL  SKIP     ms')
console.log('-'.repeat(width + 26))
for (const r of rows) {
  const mark = r.fail > 0 ? '✗' : r.crashed ? '!' : '✓'
  console.log(
    r.file.padEnd(width) +
      '  ' + String(r.pass).padStart(4) +
      '  ' + String(r.fail).padStart(4) +
      '  ' + String(r.skip).padStart(4) +
      '  ' + String(r.ms).padStart(6) +
      '  ' + mark,
  )
  for (const line of r.detail) console.log('    ' + line.slice(0, 160))
  if (r.crashed) console.log('    （进程退出码 ' + r.status + '，但没有 FAIL 行：可能是启动就崩了）')
}
console.log('-'.repeat(width + 26))
console.log(
  `合计 ${totalPass} 通过 / ${totalFail} 失败${totalSkip ? ` / ${totalSkip} 跳过` : ''}（${rows.length} 个文件）\n`,
)

process.exit(totalFail > 0 || rows.some((r) => r.crashed) ? 1 : 0)

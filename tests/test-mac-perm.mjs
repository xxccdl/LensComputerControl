import { permissionSummary } from '../engine-mac.mjs'

let fail = 0
const ok = (c, label, extra = '') => {
  console.log((c ? 'PASS ' : 'FAIL ') + label + (c ? '' : '  ' + extra))
  if (!c) fail++
}

const none = permissionSummary({ accessibility: false, screen: false })
ok(none.granted === false, '两项都没给 → 未授权')
ok(none.missing.length === 2, '缺两项', JSON.stringify(none.missing))
ok(none.panes.length === 2, '给出两个设置面板链接')
ok(none.panes[0].url.includes('Privacy_Accessibility'), '辅助功能面板 URL 正确', none.panes[0].url)
ok(none.panes[1].url.includes('Privacy_ScreenCapture'), '屏幕录制面板 URL 正确', none.panes[1].url)
ok(none.hint.includes('辅助功能') && none.hint.includes('屏幕录制'), '提示里列全缺失项', none.hint)

const onlyAx = permissionSummary({ accessibility: true, screen: false })
ok(onlyAx.missing.length === 1 && onlyAx.missing[0] === 'screen', '只缺屏幕录制', JSON.stringify(onlyAx.missing))
ok(onlyAx.panes[0].key === 'screen', '面板只给缺的那一个', JSON.stringify(onlyAx.panes))

const onlyScr = permissionSummary({ accessibility: false, screen: true })
ok(onlyScr.missing[0] === 'accessibility', '只缺辅助功能', JSON.stringify(onlyScr.missing))

const all = permissionSummary({ accessibility: true, screen: true })
ok(all.granted === true && all.missing.length === 0 && all.panes.length === 0, '两项都有 → granted，无面板')
ok(all.hint.length > 0, 'granted 时也给一句说明', all.hint)

process.exit(fail ? 1 : 0)

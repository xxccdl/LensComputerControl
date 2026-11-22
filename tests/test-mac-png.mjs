// engine-mac 的纯本地部分测试：PNG 解码/编码、画框、取色。不需要 macOS。
import { readFileSync, writeFileSync } from 'node:fs'
import { decodePng, encodePng, fillRect, strokeRect, pixelAt } from '../engine-mac.mjs'

const src = readFileSync('D:/code/computerconlex/docs/icon-180.png')
const img = decodePng(src)
console.log(`解码 OK: ${img.width}x${img.height} channels=${img.channels}`)

// 1) 不改像素的往返：解码 → 编码 → 再解码，尺寸与像素应一致
const round = decodePng(encodePng(img))
const same = round.width === img.width && round.height === img.height && Buffer.compare(round.data, img.data) === 0
console.log((same ? 'PASS' : 'FAIL') + ' PNG 编解码往返无损')

// 2) 画绿框：框上的像素变绿，框外不变
const before = pixelAt(img, 5, 5).join(',')
strokeRect(img, 40, 40, 100, 100, [34, 197, 94, 255], 4)
const onEdge = pixelAt(img, 42, 90).join(',')
const outside = pixelAt(img, 5, 5).join(',')
const okStroke = onEdge === '34,197,94' && outside === before
console.log((okStroke ? 'PASS' : 'FAIL') + ` 画框只改框内像素（边=${onEdge} 外=${outside}）`)

// 3) fillRect 半透明混合
const img2 = decodePng(src)
const px = pixelAt(img2, 90, 90).join(',')
fillRect(img2, 88, 88, 4, 4, [255, 255, 255, 128])
const mixed = pixelAt(img2, 90, 90).join(',')
console.log((mixed !== px ? 'PASS' : 'FAIL') + ` 半透明填充改变像素（${px} → ${mixed}）`)

// 4) 落盘能被再次解码（真实写盘路径）
writeFileSync('D:/code/computerconlex/_probe/out-mac-png.png', encodePng(img))
const back = decodePng(readFileSync('D:/code/computerconlex/_probe/out-mac-png.png'))
console.log((back.width === img.width ? 'PASS' : 'FAIL') + ' 写盘后仍可解码')

process.exit(same && okStroke && mixed !== px ? 0 : 1)

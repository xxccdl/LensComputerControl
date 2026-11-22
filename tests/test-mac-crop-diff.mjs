// 第二批 macOS 能力的纯像素部分：cropRect（cap --rect 用）与 diffImages（verify 用）
import { readFileSync } from 'node:fs'
import { decodePng, encodePng, cropRect, diffImages, pngSize, fillRect } from '../engine-mac.mjs'

let fail = 0
const ok = (c, label, extra = '') => {
  console.log((c ? 'PASS ' : 'FAIL ') + label + (c ? '' : '  ' + extra))
  if (!c) fail++
}

const img = decodePng(readFileSync('D:/code/computerconlex/docs/icon-180.png')) // 180x180 RGBA
const head = readFileSync('D:/code/computerconlex/docs/icon-180.png')
const size = pngSize(head)
ok(size.w === 180 && size.h === 180, 'pngSize 只读文件头就拿到尺寸', JSON.stringify(size))

// 1) 按屏幕坐标裁剪：origin (100,50)，请求 (120,70,60,40) → 图内偏移 (20,20)
const c1 = cropRect(img, { x: 120, y: 70, w: 60, h: 40 }, { x: 100, y: 50 })
ok(!!c1, 'cropRect 正常裁剪')
ok(c1 && c1.img.width === 60 && c1.img.height === 40, '裁剪尺寸正确', c1 && `${c1.img.width}x${c1.img.height}`)
ok(c1 && c1.rect.x === 120 && c1.rect.y === 70, '返回的 rect 是屏幕坐标原点', c1 && JSON.stringify(c1.rect))

// 裁剪后的像素应当等于原图对应位置
const srcPx = (x, y) => {
  const i = (y * img.width + x) * img.channels
  return [img.data[i], img.data[i + 1], img.data[i + 2]].join(',')
}
const dstPx = (x, y) => {
  const i = (y * c1.img.width + x) * c1.img.channels
  return [c1.img.data[i], c1.img.data[i + 1], c1.img.data[i + 2]].join(',')
}
ok(dstPx(0, 0) === srcPx(20, 20), '裁剪后左上角像素与原图对齐', `${dstPx(0, 0)} vs ${srcPx(20, 20)}`)
ok(dstPx(59, 39) === srcPx(79, 59), '裁剪后右下角像素与原图对齐')

// 2) 越界裁剪：贴着边界会被收缩，完全在外面返回 null
const c2 = cropRect(img, { x: 100 + 170, y: 50 + 170, w: 50, h: 50 }, { x: 100, y: 50 })
ok(c2 && c2.img.width === 10 && c2.img.height === 10, '越界部分被收缩到图内', c2 && `${c2.img.width}x${c2.img.height}`)
ok(cropRect(img, { x: 0, y: 0, w: 50, h: 50 }, { x: 100, y: 50 }) === null, '完全在区域外返回 null')

// 3) 裁剪结果还能被编码/解码（接进 cap 的写盘路径）
const back = decodePng(encodePng(c1.img))
ok(back.width === 60 && Buffer.compare(back.data, c1.img.data) === 0, '裁剪结果编码往返无损')

// 4) diffImages：同一张图 → 没变化
const same = diffImages(decodePng(head), decodePng(head))
ok(same.changed === false && same.ratio === 0, '同图差分 changed=false', JSON.stringify(same))

// 5) 只改一小块（模拟悬停高亮）→ changed=true 且 hoverSuspected=true
const hover = decodePng(head)
fillRect(hover, 40, 40, 40, 40, [255, 255, 255, 255])
const d1 = diffImages(decodePng(head), hover)
ok(d1.changed === true, '小块变化也算 changed', JSON.stringify(d1))
ok(d1.hoverSuspected === true, '小块变化被标成 hoverSuspected', JSON.stringify(d1.box))
ok(d1.box && d1.box.w <= 44 && d1.box.h <= 44, '差分框定住变化范围', JSON.stringify(d1.box))

// 6) 大面积变化（模拟真跳转）→ hoverSuspected=false
const big = decodePng(head)
fillRect(big, 0, 0, 180, 180, [255, 255, 255, 255])
const d2 = diffImages(decodePng(head), big)
ok(d2.changed === true && d2.hoverSuspected === false, '全屏变化不算悬停', JSON.stringify(d2))

// 7) 尺寸不同 → 直接判 changed
const other = cropRect(img, { x: 0, y: 0, w: 100, h: 100 }, { x: 0, y: 0 })
const d3 = diffImages(img, other.img)
ok(d3.changed === true && d3.reason === 'size-changed', '尺寸变化直接判 changed', JSON.stringify(d3))

console.log(`\n${fail ? 'FAILED ' + fail : 'ALL PASS'}`)
process.exit(fail ? 1 : 0)

// macOS 引擎的切块定位（纯像素部分）：不需要 macOS 也能验证网格选择、切分与标绘制。
import { readFileSync, writeFileSync } from 'node:fs'
import { decodePng, encodePng, bestGrid, splitCells, renderCells, pixelAt } from '../engine-mac.mjs'

let pass = 0
let fail = 0
const ok = (cond, label, extra = '') => {
  if (cond) { pass++; console.log('PASS ' + label) } else { fail++; console.log('FAIL ' + label + (extra ? '  ' + extra : '')) }
}

// 1) 3×3 的格子应当形状接近正方形，且行列都为 3
const g9 = bestGrid(9, 1200, 1200)
ok(g9.cols * g9.rows === 9, 'cells=9 切出 9 格', JSON.stringify(g9))
ok(g9.cols === 3 && g9.rows === 3, 'cells=9 → 3×3', JSON.stringify(g9))

// 2) 10 格在 16:9 屏上应当横向多于纵向，且不产生细条
const g10 = bestGrid(10, 1920, 1080)
ok(g10.cols * g10.rows === 10, 'cells=10 切出 10 格', JSON.stringify(g10))
const cellAspect = 1920 / g10.cols / (1080 / g10.rows)
ok(cellAspect < 3 && cellAspect > 1 / 3, '10 格时子格不是细条', `aspect=${cellAspect.toFixed(2)}`)

// 3) 切分覆盖整个区域、互不重叠（取并集面积等于区域面积）
const region = { x: 100, y: 50, w: 900, h: 600 }
const list = splitCells(region, 3, 3, 9)
const area = list.reduce((s, c) => s + c.w * c.h, 0)
ok(list.length === 9 && area === region.w * region.h, '9 格拼满区域且无重叠', `area=${area} vs ${region.w * region.h}`)
ok(list[0].x === 100 && list[0].y === 50, '首格左上角对齐区域原点', JSON.stringify(list[0]))

// 4) 格子编号顺序：从左到右、从上到下
const c5 = list[4]
ok(c5.x === 100 + 300 && c5.y === 50 + 200, '第 5 格在第 2 行第 2 列', JSON.stringify(c5))

// 5) path 下钻：把第 5 格当新区域再切 9 格，全部落在原第 5 格内
const sub = splitCells({ x: c5.x, y: c5.y, w: c5.w, h: c5.h }, 3, 3, 9)
ok(
  sub.every((s) => s.x >= c5.x && s.y >= c5.y && s.x + s.w <= c5.x + c5.w && s.y + s.h <= c5.y + c5.h),
  '二级切分落在所选格内',
)

// 6) 标注图：网格线为黄色 (255,196,0)，写在格边界上
const img = decodePng(readFileSync('D:/code/computerconlex/docs/icon-180.png'))
const canvas = renderCells(img, { x: 0, y: 0, w: img.width, h: img.height }, splitCells({ x: 0, y: 0, w: img.width, h: img.height }, 3, 3, 9), 2)
const edge = pixelAt(canvas, 0, 10).join(',')
ok(edge === '255,196,0', '格边界画成黄色线', edge)
ok(canvas.width === 360, '放大 2 倍后画布尺寸正确', `${canvas.width}x${canvas.height}`)

// 7) 编号底衬：每个格子左上角应有一块黑色底
const badge = pixelAt(canvas, 70, 12)
ok(badge[0] < 60 && badge[1] < 60 && badge[2] < 60, '编号底衬是深色块', badge.join(','))

writeFileSync('D:/code/computerconlex/_probe/out-mac-cells.png', encodePng(canvas))
console.log(`\n${pass} passed, ${fail} failed`)
process.exit(fail ? 1 : 0)

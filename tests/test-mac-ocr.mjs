// OCR 的纯逻辑部分：词→行合并、归一化、相似度、shortcuts 文本兜底（不需要 macOS）
import { mergeWordsIntoLines, normalizeText, similarity, parseShortcutText } from '../engine-mac.mjs'

let fail = 0
const ok = (c, label, extra = '') => {
  console.log((c ? 'PASS ' : 'FAIL ') + label + (c ? '' : '  ' + extra))
  if (!c) fail++
}

// 1) 词 → 行：同一行的两个字被切成"登"、"录"，应当合并成"登录"
const words = [
  { text: '登', x: 100, y: 200, w: 20, h: 22, conf: 0.9 },
  { text: '录', x: 122, y: 201, w: 20, h: 22, conf: 0.9 },
  { text: '取消', x: 100, y: 260, w: 44, h: 22, conf: 0.95 },
]
const lines = mergeWordsIntoLines(words)
ok(lines.length === 2, '两行词合并成 2 行', JSON.stringify(lines.map((l) => l.text)))
ok(lines[0].text === '登录', '同一行的词拼成「登录」', lines[0].text)
ok(lines[0].x === 100 && lines[0].w === 42, '行框包住整行', JSON.stringify({ x: lines[0].x, w: lines[0].w }))
ok(lines[1].text === '取消', '第二行是「取消」', lines[1].text)

// 2) 行内乱序的词按 x 排序后再拼
const messy = mergeWordsIntoLines([
  { text: '录', x: 122, y: 200, w: 20, h: 22 },
  { text: '登', x: 100, y: 200, w: 20, h: 22 },
])
ok(messy[0].text === '登录', '词序不影响拼接结果', messy[0].text)

// 3) 空输入不炸
ok(mergeWordsIntoLines([]).length === 0, '空词表返回空行')
ok(mergeWordsIntoLines([{ text: '' }]).length === 0, '空文本被过滤')

// 4) 归一化：全角数字、易混字、空白
ok(normalizeText('１２３') === '123', '全角数字转半角', normalizeText('１２３'))
ok(normalizeText('O 0 I l 1') === '00111', '易混字统一（O→0、I/l→1）', normalizeText('O 0 I l 1'))
ok(normalizeText('测 试 2 2 2') === '测试222', '去空白', normalizeText('测 试 2 2 2'))
ok(normalizeText('Safari') === '5afari', 'S→5 的口径与 Windows 侧一致', normalizeText('Safari'))

// 5) 相似度
ok(similarity('测试222', '测试222') === 1, '完全相同 = 1')
ok(similarity('测试222', '测试223') > 0.7, '差一个字仍然高相似', String(similarity('测试222', '测试223')))
ok(similarity('测试222', '发送') < 0.3, '完全不同的文本相似度低', String(similarity('测试222', '发送')))
ok(similarity('', '') === 1, '两个空串视为一致')

// 6) shortcuts 兜底：纯文本按行切成无坐标的"词"
const st = parseShortcutText('第一行\n\n第二行 内容\n')
ok(st.length === 2, '空行被丢掉', JSON.stringify(st.map((w) => w.text)))
ok(st[0].noBox === true, 'shortcuts 结果标记为无坐标', JSON.stringify(st[0]))

console.log(fail ? `\nFAILED ${fail}` : '\nALL PASS')
process.exit(fail ? 1 : 0)

# -*- coding: utf-8 -*-
"""Decode full metadata description from the 3MF, and compute geometry bounds."""
import pathlib, re, html, json

BASE = pathlib.Path(r'D:\code\computerconlex\slipper44\unpacked')
s = (BASE / '3D' / '3dmodel.model').read_text(encoding='utf-8')
out = []

for name in ('Description', 'ProfileDescription'):
    m = re.search(r'<metadata name="%s">(.*?)</metadata>' % name, s, re.S)
    if not m:
        out.append('no %s' % name)
        continue
    txt = html.unescape(html.unescape(m.group(1)))
    txt = re.sub(r'<br\s*/?>', '\n', txt)
    txt = re.sub(r'</p>', '\n', txt)
    txt = re.sub(r'<[^>]+>', '', txt)
    out.append('===== %s =====' % name)
    out.append(txt.strip())
    out.append('')

# all metadata names/values short
out.append('===== metadata list =====')
for m in re.finditer(r'<metadata name="([^"]+)"\s*/?>|<metadata name="([^"]+)">(.*?)</metadata>', s, re.S):
    nm = m.group(1) or m.group(2)
    val = (m.group(3) or '')
    if len(val) > 200:
        val = val[:200] + ' ...'
    out.append('%-24s %s' % (nm, val.strip()))

pathlib.Path(r'D:\code\computerconlex\slipper44\report_meta.txt').write_text('\n'.join(out), encoding='utf-8')
print('ok')

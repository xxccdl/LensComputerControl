# -*- coding: utf-8 -*-
"""Inspect the Bambu Studio project 3MF: units, build items, objects, bbox."""
import pathlib, re, sys, json

BASE = pathlib.Path(r'D:\code\computerconlex\slipper44\unpacked')
out = []

def w(s=''):
    out.append(str(s))

# ---------- main model ----------
main = BASE / '3D' / '3dmodel.model'
s = main.read_text(encoding='utf-8')
w('===== 3D/3dmodel.model (%d chars) =====' % len(s))

head = s[:1500]
w(head)
w()

unit = re.search(r'<model[^>]*unit="([^"]+)"', s)
w('unit attr = %s' % (unit.group(1) if unit else None))

for m in re.finditer(r'<object\b[^>]*>', s):
    w('OBJECT: ' + m.group(0))
for m in re.finditer(r'<component\b[^>]*>', s):
    w('COMPONENT: ' + m.group(0))
for m in re.finditer(r'<item\b[^>]*>', s):
    w('ITEM: ' + m.group(0))
for m in re.finditer(r'<metadata\b[^>]*>[^<]*</metadata>', s):
    w('META: ' + m.group(0)[:400])

# ---------- object files ----------
for name in ['object_1.model', 'object_2.model', 'object_3.model', 'object_4.model']:
    p = BASE / '3D' / 'Objects' / name
    t = p.read_text(encoding='utf-8')
    w()
    w('===== %s (%d chars, %.1f MB) =====' % (name, len(t), p.stat().st_size / 1e6))
    w(t[:600].replace('\n', ' '))
    u = re.search(r'<model[^>]*unit="([^"]+)"', t)
    w('unit=%s' % (u.group(1) if u else None))
    objs = re.findall(r'<object\b[^>]*>', t)
    w('objects: %d  %s' % (len(objs), objs[:5]))
    comps = re.findall(r'<component\b[^>]*>', t)
    w('components: %d  %s' % (len(comps), comps[:6]))
    items = re.findall(r'<item\b[^>]*>', t)
    w('items: %d  %s' % (len(items), items[:6]))
    w('vertices count tag: %s' % re.findall(r'<vertices[^>]*>', t)[:3])
    w('triangles count tag: %s' % re.findall(r'<triangles[^>]*>', t)[:3])
    w('tail: ' + t[-300:].replace('\n', ' '))

pathlib.Path(r'D:\code\computerconlex\slipper44\report_inspect.txt').write_text('\n'.join(out), encoding='utf-8')
print('written')

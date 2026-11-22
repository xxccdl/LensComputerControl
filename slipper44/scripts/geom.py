# -*- coding: utf-8 -*-
"""Compute per-object local bbox and world bbox (applying 3MF transforms)."""
import pathlib, re, math, json

BASE = pathlib.Path(r'D:\code\computerconlex\slipper44\unpacked')
out = []

def w(s=''):
    out.append(str(s))

def parse_matrix(txt):
    v = [float(x) for x in txt.split()]
    assert len(v) == 12, v
    L = [[v[0], v[3], v[6]],
         [v[1], v[4], v[7]],
         [v[2], v[5], v[8]]]
    T = [v[9], v[10], v[11]]
    return L, T

def apply(L, T, p):
    return [L[i][0]*p[0] + L[i][1]*p[1] + L[i][2]*p[2] + T[i] for i in range(3)]

main = (BASE / '3D' / '3dmodel.model').read_text(encoding='utf-8')

# object id -> component (path, objectid, transform)
comp_by_obj = {}
for m in re.finditer(r'<component[^>]*p:path="([^"]+)"[^>]*objectid="(\d+)"[^>]*transform="([^"]+)"', main):
    comp_by_obj_obj = m
comps = re.findall(r'<component ([^>]*)/>', main)
# map: object id (parent) -> component info
obj_blocks = re.findall(r'<object id="(\d+)"[^>]*>(.*?)</object>', main, re.S)
obj2comp = {}
for oid, body in obj_blocks:
    cm = re.search(r'<component[^>]*p:path="([^"]+)"[^>]*objectid="(\d+)"[^>]*transform="([^"]+)"', body)
    if cm:
        obj2comp[int(oid)] = (cm.group(1), int(cm.group(2)), cm.group(3))
w('object -> component: %s' % json.dumps(obj2comp, indent=1))

items = []
for m in re.finditer(r'<item ([^>]*)/>', main):
    attrs = m.group(1)
    oid = int(re.search(r'objectid="(\d+)"', attrs).group(1))
    tr = re.search(r'transform="([^"]+)"', attrs)
    items.append((oid, tr.group(1) if tr else '1 0 0 0 1 0 0 0 1 0 0 0'))

verts_cache = {}
def load_verts(path_rel, objectid):
    key = (path_rel, objectid)
    if key in verts_cache:
        return verts_cache[key]
    p = BASE / path_rel.lstrip('/').replace('/', '\\')
    txt = p.read_text(encoding='utf-8')
    m = re.search(r'<object id="%d".*?<vertices>(.*?)</vertices>' % objectid, txt, re.S)
    body = m.group(1)
    vs = []
    for vm in re.finditer(r'<vertex x="([^"]+)" y="([^"]+)" z="([^"]+)"/>', body):
        vs.append((float(vm.group(1)), float(vm.group(2)), float(vm.group(3))))
    verts_cache[key] = vs
    return vs

def bbox(pts):
    xs = [p[0] for p in pts]; ys = [p[1] for p in pts]; zs = [p[2] for p in pts]
    return (min(xs), min(ys), min(zs)), (max(xs), max(ys), max(zs))

w('')
allw = []
for oid, tr in items:
    path_rel, sub_oid, comp_tr = obj2comp[oid]
    vs = load_verts(path_rel, sub_oid)
    lo, hi = bbox(vs)
    Lc, Tc = parse_matrix(comp_tr)
    Li, Ti = parse_matrix(tr)
    # world = Li*(Lc*v + Tc) + Ti
    wv = []
    for v in vs:
        p = apply(Lc, Tc, v)
        wv.append(apply(Li, Ti, p))
    wlo, whi = bbox(wv)
    size_l = [hi[i]-lo[i] for i in range(3)]
    size_w = [whi[i]-wlo[i] for i in range(3)]
    allw.extend(wv)
    w('item objid=%d  file=%s  sub=%d' % (oid, path_rel, sub_oid))
    w('   verts=%d' % len(vs))
    w('   local bbox min=%s max=%s' % ([round(x,2) for x in lo], [round(x,2) for x in hi]))
    w('   local size       = %s' % [round(x,2) for x in size_l])
    w('   world bbox min=%s max=%s' % ([round(x,2) for x in wlo], [round(x,2) for x in whi]))
    w('   world size       = %s' % [round(x,2) for x in size_w])
    w('')

lo, hi = bbox(allw)
w('ASSEMBLY world bbox min=%s max=%s' % ([round(x,2) for x in lo], [round(x,2) for x in hi]))
w('ASSEMBLY world size      = %s' % [round(hi[i]-lo[i],2) for i in range(3)])

pathlib.Path(r'D:\code\computerconlex\slipper44\report_geom.txt').write_text('\n'.join(out), encoding='utf-8')
print('ok')

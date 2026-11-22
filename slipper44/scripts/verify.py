# -*- coding: utf-8 -*-
"""Independent verification of the rescaled project (correct 3MF row-major 4x3)."""
import pathlib, re, zipfile, io

def load(three_mf):
    z = zipfile.ZipFile(three_mf)
    out = {}
    for n in z.namelist():
        if n.endswith(('.model', '.config', '.rels', '.xml', '.json')):
            try:
                out[n] = z.read(n).decode('utf-8')
            except UnicodeDecodeError:
                pass
    return out

def mat(txt):
    v = [float(x) for x in txt.split()]
    # 3MF: p' = p * M, 4x3 row-major
    def ap(p):
        return (p[0]*v[0] + p[1]*v[3] + p[2]*v[6] + v[9],
                p[0]*v[1] + p[1]*v[4] + p[2]*v[7] + v[10],
                p[0]*v[2] + p[1]*v[5] + p[2]*v[8] + v[11])
    return ap

def verts(txt, oid):
    m = re.search(r'<object id="%d".*?<vertices>(.*?)</vertices>' % oid, txt, re.S)
    return [(float(a), float(b), float(c)) for a, b, c in
            re.findall(r'<vertex x="([^"]+)" y="([^"]+)" z="([^"]+)"', m.group(1))]

def tris(txt):
    return len(re.findall(r'<triangle ', txt))

def analyse(three_mf, label):
    files = load(three_mf)
    main = files['3D/3dmodel.model']
    obj2comp = {}
    for oid, body in re.findall(r'<object id="(\d+)"[^>]*>(.*?)</object>', main, re.S):
        cm = re.search(r'<component[^>]*p:path="([^"]+)"[^>]*objectid="(\d+)"[^>]*transform="([^"]+)"', body)
        obj2comp[int(oid)] = (cm.group(1), int(cm.group(2)), cm.group(3))
    rows = ['', '================ %s ================' % label]
    plates = []
    for m in re.finditer(r'<item ([^>]*)/>', main):
        a = m.group(1)
        oid = int(re.search(r'objectid="(\d+)"', a).group(1))
        tr = re.search(r'transform="([^"]+)"', a).group(1)
        path, sub, ctr = obj2comp[oid]
        body = files[path.lstrip('/')]
        vs = verts(body, sub)
        f1, f2 = mat(ctr), mat(tr)
        ws = [f2(f1(v)) for v in vs]
        xs = [p[0] for p in ws]; ys = [p[1] for p in ws]; zs = [p[2] for p in ws]
        bb = (min(xs), min(ys), min(zs), max(xs), max(ys), max(zs))
        size = (bb[3]-bb[0], bb[4]-bb[1], bb[5]-bb[2])
        rows.append('item %d  %-28s verts=%6d tris=%6d' % (oid, path.split('/')[-1], len(vs), tris(body)))
        rows.append('        size  = %8.2f x %8.2f x %7.2f mm' % size)
        rows.append('        bbox  = x %7.2f..%7.2f  y %7.2f..%7.2f  z %5.2f..%5.2f'
                    % (bb[0], bb[3], bb[1], bb[4], bb[2], bb[5]))
        plates.append((oid, bb, size))
    # plate-local check: plate1 origin (0,0), plate2 (307.2,0), plate3 (0,-307.2)
    origin = {6: (0, 0), 4: (307.2, 0), 2: (0, -307.2), 8: (0, -307.2)}
    rows.append('--- plate-local placement (build plate = 256 x 256) ---')
    for oid, bb, size in plates:
        ox, oy = origin[oid]
        x0, x1 = bb[0]-ox, bb[3]-ox
        y0, y1 = bb[1]-oy, bb[4]-oy
        inside = x0 >= -0.01 and y0 >= -0.01 and x1 <= 256.01 and y1 <= 256.01
        rows.append('item %d: x %6.1f..%6.1f  y %6.1f..%6.1f  min-margin=%5.1f mm  %s'
                    % (oid, x0, x1, y0, y1, min(x0, y0, 256-x1, 256-y1),
                       'ON PLATE' if inside else '*** OUT OF PLATE ***'))
    # strap separation on plate 3
    st = {oid: (bb[1], bb[4]) for oid, bb, s in plates if oid in (2, 8)}
    if 2 in st and 8 in st:
        gap = st[2][0] - st[8][1]
        rows.append('plate3 strap AABB gap = %.2f mm' % gap)
    return rows

out = []
out += analyse(r'C:\Users\65411\Downloads\ata-tpu-slide-sandal-size7-thick.3mf', 'SOURCE  size7 100%')
out += analyse(r'D:\code\computerconlex\slipper44\ata-tpu-slide-sandal-size44-thick.3mf', 'NEW  size44 115.79%')

pathlib.Path(r'D:\code\computerconlex\slipper44\report_verify.txt').write_text('\n'.join(out), encoding='utf-8')
print('ok')

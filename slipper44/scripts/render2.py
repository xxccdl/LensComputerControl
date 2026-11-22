# -*- coding: utf-8 -*-
"""Footprint preview: one panel per plate, one column per variant."""
import pathlib, re, zipfile, numpy as np
from PIL import Image, ImageDraw, ImageFont

VARIANTS = [
    ('size 7  (100 %)',      r'C:\Users\65411\Downloads\ata-tpu-slide-sandal-size7-thick.3mf'),
    ('EU 44  (115.79 %)',    r'D:\code\computerconlex\slipper44\ata-tpu-slide-sandal-size44-115.79pct.3mf'),
    ('EU 44  (110 %, snug)', r'D:\code\computerconlex\slipper44\ata-tpu-slide-sandal-size44-snug-110pct.3mf'),
]
PLATES = [
    ('plate 1 - thick footbed L', [6], (0.0, 0.0)),
    ('plate 3 - instep bands',    [2, 8], (0.0, -307.2)),
]
ORIGIN = {6: (0, 0), 4: (307.2, 0), 2: (0, -307.2), 8: (0, -307.2)}

def read_entries(p):
    z = zipfile.ZipFile(p)
    return {n: z.read(n).decode('utf-8') for n in z.namelist() if n.endswith('.model')}

def mat4x3(txt):
    v = [float(x) for x in txt.split()]
    return lambda p: (p[0]*v[0] + p[1]*v[3] + p[2]*v[6] + v[9],
                      p[0]*v[1] + p[1]*v[4] + p[2]*v[7] + v[10],
                      p[0]*v[2] + p[1]*v[5] + p[2]*v[8] + v[11])

def items(files):
    main = files['3D/3dmodel.model']
    obj2comp = {}
    for oid, body in re.findall(r'<object id="(\d+)"[^>]*>(.*?)</object>', main, re.S):
        cm = re.search(r'<component[^>]*p:path="([^"]+)"[^>]*objectid="(\d+)"[^>]*transform="([^"]+)"', body)
        obj2comp[int(oid)] = (cm.group(1), int(cm.group(2)), cm.group(3))
    res = {}
    for m in re.finditer(r'<item ([^>]*)/>', main):
        a = m.group(1)
        oid = int(re.search(r'objectid="(\d+)"', a).group(1))
        tr = re.search(r'transform="([^"]+)"', a).group(1)
        path, sub, ctr = obj2comp[oid]
        body = files[path.lstrip('/')]
        blk = re.search(r'<object id="%d".*?<vertices>(.*?)</vertices>' % sub, body, re.S).group(1)
        v = [[float(x) for x in t] for t in
             re.findall(r'<vertex x="([^"]+)" y="([^"]+)" z="([^"]+)"', blk)]
        f1, f2 = mat4x3(ctr), mat4x3(tr)
        res[oid] = np.array([f2(f1(p)) for p in v])
    return res

def hull2d(pts):
    p = pts[np.lexsort((pts[:, 1], pts[:, 0]))]

    def cross(o, a, b):
        return (a[0]-o[0])*(b[1]-o[1]) - (a[1]-o[1])*(b[0]-o[0])

    def half(ps):
        h = []
        for q in ps:
            while len(h) >= 2 and cross(h[-2], h[-1], q) <= 0:
                h.pop()
            h.append(q)
        return h
    return np.array(half(p)[:-1] + half(p[::-1])[:-1])

PPMM = 1.45
PAN = int(256 * PPMM)
ML, MT = 16, 74
COLW = PAN + 2 * ML
ROWGAP = 52
W = COLW * len(VARIANTS) + ML
H = MT + (PAN + ROWGAP) * len(PLATES) + 30
img = Image.new('RGB', (W, H), (242, 243, 245))
d = ImageDraw.Draw(img, 'RGBA')
try:
    f_big = ImageFont.truetype('arialbd.ttf', 20)
    f_md = ImageFont.truetype('arial.ttf', 15)
    f_sm = ImageFont.truetype('arial.ttf', 13)
except OSError:
    f_big = f_md = f_sm = ImageFont.load_default()

data = [items(read_entries(p)) for _, p in VARIANTS]

for ci, (title, _) in enumerate(VARIANTS):
    d.text((ML + ci * COLW, 16), title, font=f_big, fill=(15, 15, 15))

for ri, (rowtitle, oids, _) in enumerate(PLATES):
    oy = MT + ri * (PAN + ROWGAP)
    for ci in range(len(VARIANTS)):
        ox = ML + ci * COLW
        d.rectangle([ox, oy, ox + PAN, oy + PAN], fill=(252, 252, 252),
                    outline=(70, 70, 70), width=2)
        for g in range(32, 256, 32):
            d.line([ox + g*PPMM, oy, ox + g*PPMM, oy + PAN], fill=(228, 228, 228))
            d.line([ox, oy + g*PPMM, ox + PAN, oy + g*PPMM], fill=(228, 228, 228))
        if ci == 0:
            d.text((ox - 4, oy - 22), rowtitle, font=f_md, fill=(60, 60, 60))
        for oid in oids:
            w = data[ci][oid]
            ox0, oy0 = ORIGIN[oid]
            pts = np.column_stack([w[:, 0] - ox0, w[:, 1] - oy0])
            h = hull2d(pts)
            poly = [(ox + x*PPMM, oy + PAN - y*PPMM) for x, y in h]
            col = (0, 132, 200) if oid == 6 else (232, 138, 0)
            d.polygon(poly, fill=col + (52,), outline=col + (255,))
        w = data[ci][oids[0]]
        x0, x1 = w[:, 0].min() - ORIGIN[oids[0]][0], w[:, 0].max() - ORIGIN[oids[0]][0]
        y0, y1 = w[:, 1].min() - ORIGIN[oids[0]][1], w[:, 1].max() - ORIGIN[oids[0]][1]
        m = min(x0, y0, 256 - x1, 256 - y1)
        d.text((ox + 4, oy + PAN - 20), '%.0f x %.0f mm  margin %.1f' % (x1-x0, y1-y0, m),
               font=f_sm, fill=(30, 30, 30))

# 270 mm foot ruler under the first row
oy = MT + PAN + 6
d.line([ML, oy, ML + 270*PPMM, oy], fill=(200, 40, 40), width=3)
for xx in (ML, ML + 270*PPMM):
    d.line([xx, oy-5, xx, oy+5], fill=(200, 40, 40), width=2)
d.text((ML + 270*PPMM + 8, oy - 8), 'EU44 foot = 270 mm (reference)', font=f_sm, fill=(200, 40, 40))

img.save(r'D:\code\computerconlex\slipper44\preview_fit.png')
print('ok', img.size)

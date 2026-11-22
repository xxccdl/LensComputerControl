# -*- coding: utf-8 -*-
"""Verify the two generated 44-size projects and render a footprint preview."""
import pathlib, re, zipfile, numpy as np
from PIL import Image, ImageDraw, ImageFont

SRC = {
    'size7 100%':   r'C:\Users\65411\Downloads\ata-tpu-slide-sandal-size7-thick.3mf',
    'EU44 115.79%': r'D:\code\computerconlex\slipper44\ata-tpu-slide-sandal-size44-115.79pct.3mf',
    'EU44 110%':    r'D:\code\computerconlex\slipper44\ata-tpu-slide-sandal-size44-snug-110pct.3mf',
}
ORIGIN = {6: (0, 0), 4: (307.2, 0), 2: (0, -307.2), 8: (0, -307.2)}
NAME = {2: 'band L (plate 3)', 8: 'band R (plate 3)',
        6: 'footbed L (plate 1)', 4: 'footbed R (plate 2)'}

def read_entries(p):
    z = zipfile.ZipFile(p)
    out = {}
    for n in z.namelist():
        if n.endswith(('.model', '.config')):
            out[n] = z.read(n).decode('utf-8')
    return out

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
        v = np.array([[float(x) for x in t] for t in
                      re.findall(r'<vertex x="([^"]+)" y="([^"]+)" z="([^"]+)"', blk)])
        f1, f2 = mat4x3(ctr), mat4x3(tr)
        w = np.array([f2(f1(p)) for p in v])
        res[oid] = w
    return res

def hull2d(pts):
    p = pts[np.lexsort((pts[:, 1], pts[:, 0]))]
    def half(ps):
        h = []
        for q in ps:
            while len(h) >= 2 and np.cross(h[-1] - h[-2], q - h[-2]) <= 0:
                h.pop()
            h.append(q)
        return h
    lower, upper = half(p), half(p[::-1])
    return np.array(lower[:-1] + upper[:-1])

out = []
data = {}
for label, path in SRC.items():
    files = read_entries(path)
    its = items(files)
    data[label] = its
    out.append('===== %s =====' % label)
    for oid in (6, 4, 2, 8):
        w = its[oid]
        mn, mx = w.min(0), w.max(0)
        ox, oy = ORIGIN[oid]
        x0, x1 = mn[0] - ox, mx[0] - ox
        y0, y1 = mn[1] - oy, mx[1] - oy
        out.append('  %-20s size %7.2f x %7.2f x %6.2f  |  plate x %6.1f..%6.1f  y %6.1f..%6.1f  margin %5.1f  %s'
                   % (NAME[oid], mx[0]-mn[0], mx[1]-mn[1], mx[2]-mn[2],
                      x0, x1, y0, y1, min(x0, y0, 256-x1, 256-y1),
                      'OK' if min(x0, y0, 256-x1, 256-y1) >= 0 else 'OUT'))
    out.append('')
txt = '\n'.join(out)
pathlib.Path(r'D:\code\computerconlex\slipper44\report_verify2.txt').write_text(txt, encoding='utf-8')
print(txt)

# ------------------------------------------------------------------ preview
PPMM = 2.0
PAN = int(256 * PPMM) + 56
PAD = 18
W = PAD + len(SRC) * (PAN + PAD)
H = PAN + 96
img = Image.new('RGB', (W, H), (238, 240, 242))
d = ImageDraw.Draw(img, 'RGBA')
try:
    f_big = ImageFont.truetype('arialbd.ttf', 22)
    f_sm = ImageFont.truetype('arial.ttf', 16)
except OSError:
    f_big = f_sm = ImageFont.load_default()

for i, (label, its) in enumerate(data.items()):
    ox_px = PAD + i * (PAN + PAD)
    oy_px = 56
    # build plate
    d.rectangle([ox_px, oy_px, ox_px + PAN, oy_px + PAN], fill=(250, 250, 250),
                outline=(90, 90, 90), width=2)
    for g in range(32, 256, 32):
        gx = ox_px + g * PPMM
        gy = oy_px + g * PPMM
        d.line([gx, oy_px, gx, oy_px + PAN], fill=(226, 226, 226))
        d.line([ox_px, gy, ox_px + PAN, gy], fill=(226, 226, 226))
    d.text((ox_px, 22), label, font=f_big, fill=(20, 20, 20))

    # footprints (plate-local)
    for oid, colour in ((6, (0, 130, 200)), (4, (0, 130, 200)),
                        (2, (230, 140, 0)), (8, (230, 140, 0))):
        w = its[oid]
        ox, oy = ORIGIN[oid]
        pts = np.column_stack([w[:, 0] - ox, w[:, 1] - oy])
        h = hull2d(pts)
        poly = [(ox_px + x * PPMM, oy_px + PAN - y * PPMM) for x, y in h]
        d.polygon(poly, fill=colour + (46,), outline=colour + (255,))

    # 270 mm reference ruler (EU44 foot length)
    y_r = oy_px + PAN + 16
    d.line([ox_px, y_r, ox_px + 270 * PPMM, y_r], fill=(200, 30, 30), width=3)
    d.line([ox_px, y_r - 5, ox_px, y_r + 5], fill=(200, 30, 30), width=2)
    d.line([ox_px + 270 * PPMM, y_r - 5, ox_px + 270 * PPMM, y_r + 5], fill=(200, 30, 30), width=2)
    d.text((ox_px + 2, y_r + 8), 'EU44 foot 270 mm', font=f_sm, fill=(200, 30, 30))

    # size caption
    w = its[6]
    cap = 'footbed %.0f x %.0f mm' % (np.ptp(w[:, 0]), np.ptp(w[:, 1]))
    d.text((ox_px, oy_px + PAN + 34), cap, font=f_sm, fill=(40, 40, 40))

img.save(r'D:\code\computerconlex\slipper44\preview_fit.png')
print('preview written')

# -*- coding: utf-8 -*-
"""
Rescale the Bambu Studio project 3MF: US7 (100%)  ->  EU44 (115.79%).

Method
------
* Every mesh vertex is scaled by k = 1.1579 about the object's local origin.
  For all four parts the local bounding box is exactly centred on that origin,
  so this is a pure "scale about own centre".
* The build-item translation T (which equals the object's world centre) is then
  remapped per plate:  T' = k*T + (1-k)*P   with P = the plate centre.
  That keeps every part centred on its own plate and preserves the designer's
  relative arrangement inside plate 3 (the two instep bands stay 1.1 mm apart,
  exactly the same relationship as in the original 100 % file).
* Z is scaled from the bed upward:  T'z = k*Tz  -> every part still sits on z=0.

Verified numbers to reproduce (Bambu Studio reported them for 100%):
  footbed  AABB 197.42 x 213.50 x 16.50  -> 228.59 x 247.21 x 19.11
  band     AABB 182.78 x  91.78 x  5.60  -> 211.63 x 106.27 x  6.49
"""
import pathlib, re, zipfile, shutil, os

K = 1.1579                       # EU44 per the model's own size chart
SRC_DIR = pathlib.Path(r'D:\code\computerconlex\slipper44\unpacked')
OUT_3MF = pathlib.Path(r'D:\code\computerconlex\slipper44\ata-tpu-slide-sandal-size44-thick.3mf')
COPY_TO = pathlib.Path(r'C:\Users\65411\Downloads\ata-tpu-slide-sandal-size44-thick.3mf')

# item objectid -> (plate centre in world coords)
PLATE_PIVOT = {
    2: (128.0, -179.2),    # plate 3  (instep band left)
    8: (128.0, -179.2),    # plate 3  (instep band right)
    6: (128.0,   128.0),   # plate 1  thick footbed left
    4: (435.2,   128.0),   # plate 2  thick footbed right
}

VERT_RE = re.compile(r'<vertex x="(-?[0-9.eE+]+)" y="(-?[0-9.eE+]+)" z="(-?[0-9.eE+]+)"')

def scale_obj_file(path):
    txt = path.read_text(encoding='utf-8')
    n = [0]
    def rep(m):
        n[0] += 1
        x = float(m.group(1)) * K
        y = float(m.group(2)) * K
        z = float(m.group(3)) * K
        return '<vertex x="%.6f" y="%.6f" z="%.6f"' % (x, y, z)
    out = VERT_RE.sub(rep, txt)
    path.write_text(out, encoding='utf-8')
    return n[0]

# ---------------------------------------------------------------- meshes
report = []
for name in ('object_1.model', 'object_2.model', 'object_3.model', 'object_4.model'):
    p = SRC_DIR / '3D' / 'Objects' / name
    cnt = scale_obj_file(p)
    report.append('%s: %d vertices scaled x%.4f' % (name, cnt, K))

# ---------------------------------------------------------------- main model
main = SRC_DIR / '3D' / '3dmodel.model'
s = main.read_text(encoding='utf-8')

def new_translation(oid, tx, ty, tz):
    px, py = PLATE_PIVOT[oid]
    nx = K * tx + (1 - K) * px
    ny = K * ty + (1 - K) * py
    nz = K * tz
    return nx, ny, nz

def fix_item(m):
    attrs, tr = m.group(1), m.group(2)
    oid = int(re.search(r'objectid="(\d+)"', attrs).group(1))
    v = [float(x) for x in tr.split()]
    if len(v) != 12 or oid not in PLATE_PIVOT:
        return m.group(0)
    nx, ny, nz = new_translation(oid, v[9], v[10], v[11])
    v[9], v[10], v[11] = nx, ny, nz
    new_tr = ' '.join('%.6f' % x for x in v[:9]) + ' %.5f %.5f %.5f' % (nx, ny, nz)
    attrs_new = attrs.replace('transform="%s"' % tr, 'transform="%s"' % new_tr)
    report.append('item %d: T (%.3f, %.3f, %.3f) -> (%.3f, %.3f, %.3f)'
                  % (oid, v[9], v[10], v[11], nx, ny, nz))
    return '<item %s/>' % attrs_new

s2 = re.sub(r'<item ([^>]*?transform="([^"]+)")[^>]*/>', fix_item, s)

# label the profile so the project is self-describing
s2 = s2.replace('厚鞋底 - 尺码 7 (100%) - 更高质量 (3层墙, 4顶层)',
                '厚鞋底 - 尺码 44 (115.79%) - 更高质量 (3层墙, 4顶层)')
main.write_text(s2, encoding='utf-8')

# ---------------------------------------------------------------- part names
ms = SRC_DIR / 'Metadata' / 'model_settings.config'
t = ms.read_text(encoding='utf-8')
t = t.replace('size7', 'size44')
ms.write_text(t, encoding='utf-8')

# ---------------------------------------------------------------- repack
if OUT_3MF.exists():
    OUT_3MF.unlink()
with zipfile.ZipFile(OUT_3MF, 'w', zipfile.ZIP_DEFLATED, compresslevel=6) as z:
    for root, dirs, files in os.walk(SRC_DIR):
        dirs.sort()
        for f in sorted(files):
            full = pathlib.Path(root) / f
            rel = full.relative_to(SRC_DIR).as_posix()
            z.write(full, rel)
shutil.copy2(OUT_3MF, COPY_TO)

report.append('')
report.append('written: %s (%.1f MB)' % (OUT_3MF, OUT_3MF.stat().st_size / 1e6))
report.append('copy   : %s' % COPY_TO)
pathlib.Path(r'D:\code\computerconlex\slipper44\report_scale.txt').write_text(
    '\n'.join(report), encoding='utf-8')
print('\n'.join(report))

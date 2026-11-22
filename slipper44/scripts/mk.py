# -*- coding: utf-8 -*-
"""Generic re-scaler: make a size-N project 3MF from the original size-7 3MF.

usage: python mk.py <scale_percent> <out_path>
"""
import pathlib, re, sys, zipfile, shutil, os, tempfile

SRC = pathlib.Path(r'C:\Users\65411\Downloads\ata-tpu-slide-sandal-size7-thick.3mf')
PLATE_PIVOT = {2: (128.0, -179.2), 8: (128.0, -179.2),
               6: (128.0, 128.0), 4: (435.2, 128.0)}
VERT_RE = re.compile(r'<vertex x="(-?[0-9.eE+]+)" y="(-?[0-9.eE+]+)" z="(-?[0-9.eE+]+)"')

def build(k, out_path, label):
    work = pathlib.Path(tempfile.mkdtemp(prefix='rescale_'))
    with zipfile.ZipFile(SRC) as z:
        z.extractall(work)

    for p in (work / '3D' / 'Objects').glob('*.model'):
        txt = p.read_text(encoding='utf-8')
        out = VERT_RE.sub(lambda m: '<vertex x="%.6f" y="%.6f" z="%.6f"' % (
            float(m.group(1)) * k, float(m.group(2)) * k, float(m.group(3)) * k), txt)
        p.write_text(out, encoding='utf-8')

    main = work / '3D' / '3dmodel.model'
    s = main.read_text(encoding='utf-8')

    def fix_item(m):
        attrs, tr = m.group(1), m.group(2)
        oid = int(re.search(r'objectid="(\d+)"', attrs).group(1))
        v = [float(x) for x in tr.split()]
        if len(v) != 12 or oid not in PLATE_PIVOT:
            return m.group(0)
        px, py = PLATE_PIVOT[oid]
        v[9] = k * v[9] + (1 - k) * px
        v[10] = k * v[10] + (1 - k) * py
        v[11] = k * v[11]
        new_tr = ' '.join('%.6f' % x for x in v)
        return '<item %s/>' % attrs.replace('transform="%s"' % tr, 'transform="%s"' % new_tr)

    s = re.sub(r'<item ([^>]*?transform="([^"]+)")[^>]*/>', fix_item, s)
    s = s.replace('厚鞋底 - 尺码 7 (100%) - 更高质量 (3层墙, 4顶层)', label)
    main.write_text(s, encoding='utf-8')

    ms = work / 'Metadata' / 'model_settings.config'
    ms.write_text(ms.read_text(encoding='utf-8').replace('size7', 'size44'), encoding='utf-8')

    out_path.parent.mkdir(parents=True, exist_ok=True)
    if out_path.exists():
        out_path.unlink()
    with zipfile.ZipFile(out_path, 'w', zipfile.ZIP_DEFLATED, compresslevel=6) as z:
        for root, dirs, files in os.walk(work):
            dirs.sort()
            for f in sorted(files):
                full = pathlib.Path(root) / f
                z.write(full, full.relative_to(work).as_posix())
    shutil.rmtree(work, ignore_errors=True)
    print('%s  k=%.4f  %.1f MB' % (out_path.name, k, out_path.stat().st_size / 1e6))

if __name__ == '__main__':
    pct = float(sys.argv[1])
    out = pathlib.Path(sys.argv[2])
    label = sys.argv[3] if len(sys.argv) > 3 else '厚鞋底 - 尺码 44 - 更高质量 (3层墙, 4顶层)'
    build(pct / 100.0, out, label)

# -*- coding: utf-8 -*-
"""Is the 'floating area' warning inherent to the model?  Measure downward faces."""
import zipfile, re, numpy as np, pathlib

FILES = {
    'size7 100%':   r'C:\Users\65411\Downloads\ata-tpu-slide-sandal-size7-thick.3mf',
    'EU44 115.79%': r'D:\code\computerconlex\slipper44\ata-tpu-slide-sandal-size44-115.79pct.3mf',
}
PART = {'object_1.model': 'footbed L (slide-11 .L.stl)', 'object_3.model': 'band L'}

def mat4x3(txt):
    v = [float(x) for x in txt.split()]
    return lambda p: np.column_stack([
        p[:, 0]*v[0] + p[:, 1]*v[3] + p[:, 2]*v[6] + v[9],
        p[:, 0]*v[1] + p[:, 1]*v[4] + p[:, 2]*v[7] + v[10],
        p[:, 0]*v[2] + p[:, 1]*v[5] + p[:, 2]*v[8] + v[11]])

rows = []
for label, path in FILES.items():
    z = zipfile.ZipFile(path)
    main = z.read('3D/3dmodel.model').decode('utf-8')
    obj2comp = {}
    for oid, body in re.findall(r'<object id="(\d+)"[^>]*>(.*?)</object>', main, re.S):
        cm = re.search(r'<component[^>]*p:path="([^"]+)"[^>]*objectid="(\d+)"[^>]*transform="([^"]+)"', body)
        obj2comp[int(oid)] = (cm.group(1), int(cm.group(2)), cm.group(3))
    items = {}
    for m in re.finditer(r'<item ([^>]*)/>', main):
        a = m.group(1)
        oid = int(re.search(r'objectid="(\d+)"', a).group(1))
        items[oid] = re.search(r'transform="([^"]+)"', a).group(1)

    for oid, (mpath, sub, ctr) in obj2comp.items():
        fname = mpath.split('/')[-1]
        if fname not in PART:
            continue
        body = z.read(mpath.lstrip('/')).decode('utf-8')
        vb = re.search(r'<vertices>(.*?)</vertices>', body, re.S).group(1)
        tb = re.search(r'<triangles>(.*?)</triangles>', body, re.S).group(1)
        V = np.array([[float(x) for x in t] for t in
                      re.findall(r'<vertex x="([^"]+)" y="([^"]+)" z="([^"]+)"', vb)])
        T = np.array([[int(a), int(b), int(c)] for a, b, c in
                      re.findall(r'<triangle v1="(\d+)" v2="(\d+)" v3="(\d+)"', tb)])
        V = mat4x3(items[oid])(mat4x3(ctr)(V))
        p0, p1, p2 = V[T[:, 0]], V[T[:, 1]], V[T[:, 2]]
        n = np.cross(p1 - p0, p2 - p0)
        ln = np.linalg.norm(n, axis=1)
        area = ln / 2.0
        nz = n[:, 2] / np.maximum(ln, 1e-12)
        down = nz < -0.7                      # faces pointing down (underside)
        zmin = np.minimum(np.minimum(p0[:, 2], p1[:, 2]), p2[:, 2])
        zmax = np.maximum(np.maximum(p0[:, 2], p1[:, 2]), p2[:, 2])
        a_down = area[down].sum()
        a_contact = area[down & (zmax < 0.35)].sum()
        a_hang = area[down & (zmin > 0.35)].sum()
        z_all = V[:, 2]
        rows.append('%-14s %-26s  z %6.2f..%6.2f  tris=%6d' % (label, PART[fname], z_all.min(), z_all.max(), len(T)))
        rows.append('               down-facing area %9.1f mm2 | on bed %9.1f (%4.1f%%) | suspended %9.1f (%4.1f%%)'
                    % (a_down, a_contact, 100*a_contact/max(a_down,1e-9),
                       a_hang, 100*a_hang/max(a_down,1e-9)))
        # how high does the underside start, above the bed
        if a_hang > 0:
            hh = zmin[down & (zmin > 0.35)]
            rows.append('               suspended underside starts at z >= %.2f mm (mean %.2f)'
                        % (hh.min(), hh.mean()))

pathlib.Path(r'D:\code\computerconlex\slipper44\report_islands.txt').write_text('\n'.join(rows), encoding='utf-8')
print('\n'.join(rows))

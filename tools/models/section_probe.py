"""Measures evaluated vertices of one object inside an axis-aligned slab (Blender world coordinates).

    blender -b model.blend --python tools/models/section_probe.py -- <object> <axis x|y|z> <min> <max> [...more slabs]

Example: fuselage cross-section at mid-length, and the stabilizer tips:
    ... -- Fuselage y -0.2 0.2   Fuselage x 1.5 2.0
"""
import sys

import bpy

argv = sys.argv[sys.argv.index("--") + 1:]
depsgraph = bpy.context.evaluated_depsgraph_get()
for i in range(0, len(argv), 4):
    name, axis, lo, hi = argv[i], argv[i + 1], float(argv[i + 2]), float(argv[i + 3])
    o = bpy.data.objects[name]
    ev = o.evaluated_get(depsgraph)
    me = ev.to_mesh()
    k = "xyz".index(axis)
    pts = [ev.matrix_world @ v.co for v in me.vertices]
    sel = [p for p in pts if lo <= p[k] <= hi]
    ev.to_mesh_clear()
    if not sel:
        print(f"[{name} {axis} {lo}..{hi}] no vertices")
        continue
    ext = [(min(p[j] for p in sel), max(p[j] for p in sel)) for j in range(3)]
    print(f"[{name} {axis} {lo}..{hi}] n={len(sel)}  x {ext[0][0]:.3f}..{ext[0][1]:.3f}  "
          f"y {ext[1][0]:.3f}..{ext[1][1]:.3f}  z {ext[2][0]:.3f}..{ext[2][1]:.3f}")

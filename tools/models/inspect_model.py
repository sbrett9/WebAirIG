"""Blender-side model inspector. Prints mesh objects, world-space sizes, materials and texture links.

    blender -b model.blend --python tools/models/inspect_model.py
    blender -b --python tools/models/inspect_model.py -- model.fbx|.obj|.glb
"""
import os
import sys

import bpy
from mathutils import Vector

argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
if argv:
    path = os.path.abspath(argv[0])
    bpy.ops.wm.read_factory_settings(use_empty=True)
    ext = os.path.splitext(path)[1].lower()
    if ext == ".fbx":
        bpy.ops.import_scene.fbx(filepath=path)
    elif ext in (".glb", ".gltf"):
        bpy.ops.import_scene.gltf(filepath=path)
    elif ext == ".obj":
        bpy.ops.wm.obj_import(filepath=path)
    else:
        raise SystemExit(f"unsupported: {ext}")


def world_bbox(objs):
    lo = Vector((1e18, 1e18, 1e18))
    hi = Vector((-1e18, -1e18, -1e18))
    for o in objs:
        for corner in o.bound_box:
            w = o.matrix_world @ Vector(corner)
            lo = Vector(map(min, lo, w))
            hi = Vector(map(max, hi, w))
    return lo, hi


meshes = [o for o in bpy.data.objects if o.type == "MESH"]
roots = [o for o in bpy.data.objects if o.parent is None]
print("=" * 70)
print(f"unit scale {bpy.context.scene.unit_settings.scale_length}, {len(bpy.data.objects)} objects, "
      f"{len(meshes)} meshes, {sum(len(o.data.vertices) for o in meshes)} vertices")
if meshes:
    lo, hi = world_bbox(meshes)
    size = hi - lo
    print(f"scene bbox (Blender Z-up): x {lo.x:.2f}..{hi.x:.2f} ({size.x:.2f})  "
          f"y {lo.y:.2f}..{hi.y:.2f} ({size.y:.2f})  z {lo.z:.2f}..{hi.z:.2f} ({size.z:.2f})")
print("root objects:")
for r in roots[:40]:
    kids = [c for c in r.children_recursive if c.type == "MESH"] + ([r] if r.type == "MESH" else [])
    if not kids:
        print(f"  {r.name!r} ({r.type}) no meshes")
        continue
    lo, hi = world_bbox(kids)
    c = (lo + hi) / 2
    s = hi - lo
    verts = sum(len(k.data.vertices) for k in kids)
    print(f"  {r.name!r} ({r.type}) meshes={len(kids)} verts={verts} center=({c.x:.2f},{c.y:.2f},{c.z:.2f}) "
          f"size=({s.x:.2f},{s.y:.2f},{s.z:.2f})")
# Per-object bounding boxes of the *evaluated* geometry (after lattice/armature/modifiers), visible objects only.
depsgraph = bpy.context.evaluated_depsgraph_get()
rows = []
for o in meshes:
    if not o.visible_get() or o.hide_render:
        continue
    ev = o.evaluated_get(depsgraph)
    me = ev.to_mesh()
    if len(me.vertices) == 0:
        ev.to_mesh_clear()
        continue
    pts = [ev.matrix_world @ v.co for v in me.vertices]
    lo = Vector((min(p.x for p in pts), min(p.y for p in pts), min(p.z for p in pts)))
    hi = Vector((max(p.x for p in pts), max(p.y for p in pts), max(p.z for p in pts)))
    rows.append((max(hi - lo), o.name, lo, hi, len(pts)))
    ev.to_mesh_clear()
print("visible objects, evaluated geometry (largest first):")
for _, name, lo, hi, n in sorted(rows, key=lambda r: -r[0])[:15]:
    c = (lo + hi) / 2
    print(f"  {name!r:40} verts={n:6d} x {lo.x:6.2f}..{hi.x:6.2f}  y {lo.y:6.2f}..{hi.y:6.2f}  z {lo.z:6.2f}..{hi.z:6.2f}  "
          f"center=({c.x:.2f},{c.y:.2f},{c.z:.2f})")
print("materials:", ", ".join(m.name for m in bpy.data.materials)[:600])
print("images:")
for img in bpy.data.images:
    p = bpy.path.abspath(img.filepath) if img.filepath else ""
    state = "packed" if img.packed_file else ("ok" if p and os.path.exists(p) else "MISSING")
    print(f"  {img.name!r}: {img.filepath} [{state}]")
print("=" * 70)

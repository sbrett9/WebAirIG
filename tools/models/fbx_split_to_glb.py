"""Splits a multi-model FBX (e.g. a weapon pack laid out side by side) into one centered .glb per model.

    blender -b --python tools/models/fbx_split_to_glb.py -- input.fbx out_dir

Root objects whose names end in "_glass" (any case) are merged into the model whose name they start with
(seeker windows). Each group is moved so its bounding-box center sits at the origin before export, so the entity
origin is the model center. Orientation is left unchanged (check with preview_views.py).
"""
import os
import re
import sys

import bpy
from mathutils import Vector

argv = sys.argv[sys.argv.index("--") + 1:]
src, out_dir = os.path.abspath(argv[0]), os.path.abspath(argv[1])
os.makedirs(out_dir, exist_ok=True)

bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.fbx(filepath=src)

roots = [o for o in bpy.data.objects if o.parent is None and o.type == "MESH"]
glass = [o for o in roots if re.search(r"_glass$", o.name, re.IGNORECASE)]
mains = [o for o in roots if o not in glass]
groups = {m.name: [m] for m in mains}
for g in glass:
    base = re.sub(r"_glass$", "", g.name, flags=re.IGNORECASE)
    owner = next((m for m in mains if m.name.lower() == base.lower()), None)
    if owner is None:
        print(f"warning: no owner for {g.name}, exporting it alone")
        groups[g.name] = [g]
    else:
        groups[owner.name].append(g)


def bbox(objs):
    lo = Vector((1e18,) * 3)
    hi = Vector((-1e18,) * 3)
    for o in objs:
        for c in o.bound_box:
            w = o.matrix_world @ Vector(c)
            lo = Vector(map(min, lo, w))
            hi = Vector(map(max, hi, w))
    return lo, hi


for name, objs in groups.items():
    lo, hi = bbox(objs)
    center = (lo + hi) / 2
    saved = [(o, o.location.copy()) for o in objs]
    for o in objs:
        o.location -= center
    bpy.ops.object.select_all(action="DESELECT")
    for o in objs:
        o.select_set(True)
    file_name = re.sub(r"[^A-Za-z0-9_-]+", "_", name).strip("_") + ".glb"
    path = os.path.join(out_dir, file_name)
    bpy.ops.export_scene.gltf(filepath=path, export_format="GLB", use_selection=True, export_apply=True,
                              export_cameras=False, export_lights=False)
    size = hi - lo
    print(f"wrote {file_name}: size x {size.x:.2f} y {size.y:.2f} z {size.z:.2f} (Blender axes), parts {[o.name for o in objs]}")
    for o, loc in saved:
        o.location = loc

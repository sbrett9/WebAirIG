"""Renders orthographic TOP and SIDE previews of a model's visible meshes (Workbench), for checking orientation,
scale and origin before conversion.

    blender -b model.blend --python tools/models/preview_views.py -- out_prefix
    blender -b --python tools/models/preview_views.py -- out_prefix model.fbx

TOP:  camera above looking down -Z; image right = +X, image up = +Y.
SIDE: camera at +X looking toward -X; image right = +Y, image up = +Z.
A red cube marks the object origin (0,0,0); grid squares are 1 Blender unit.
"""
import math
import os
import sys

import bpy
from mathutils import Vector

argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
prefix = os.path.abspath(argv[0])
if len(argv) > 1:
    bpy.ops.wm.read_factory_settings(use_empty=True)
    path = os.path.abspath(argv[1])
    if path.lower().endswith(".fbx"):
        bpy.ops.import_scene.fbx(filepath=path)
    else:
        bpy.ops.import_scene.gltf(filepath=path)

scene = bpy.context.scene
meshes = [o for o in scene.objects if o.type == "MESH" and o.visible_get() and not o.hide_render]
for o in scene.objects:
    if o.type == "MESH" and o not in meshes:
        o.hide_render = True

lo = Vector((1e18,) * 3)
hi = Vector((-1e18,) * 3)
for o in meshes:
    for c in o.bound_box:
        w = o.matrix_world @ Vector(c)
        lo = Vector(map(min, lo, w))
        hi = Vector(map(max, hi, w))
center = (lo + hi) / 2
size = hi - lo
print(f"visible meshes: {len(meshes)}; bbox lo {tuple(round(v, 2) for v in lo)} hi {tuple(round(v, 2) for v in hi)}")

# Origin marker.
bpy.ops.mesh.primitive_cube_add(size=max(size) * 0.02, location=(0, 0, 0))
marker = bpy.context.active_object
mat = bpy.data.materials.new("OriginMarker")
mat.diffuse_color = (1, 0, 0, 1)
marker.data.materials.append(mat)

scene.render.engine = "BLENDER_WORKBENCH"
scene.display.shading.light = "STUDIO"
scene.display.shading.color_type = "MATERIAL"
scene.render.resolution_x = 900
scene.render.resolution_y = 900
scene.render.film_transparent = False
scene.world = scene.world or bpy.data.worlds.new("World")

cam_data = bpy.data.cameras.new("PreviewCam")
cam_data.type = "ORTHO"
cam = bpy.data.objects.new("PreviewCam", cam_data)
scene.collection.objects.link(cam)
scene.camera = cam
span = max(size) * 1.15
cam_data.ortho_scale = span
cam_data.clip_end = span * 10

views = {
    "top": (center + Vector((0, 0, span * 2)), (0.0, 0.0, 0.0)),
    "side": (center + Vector((span * 2, 0, 0)), (math.pi / 2, 0.0, math.pi / 2)),
}
for name, (loc, rot) in views.items():
    cam.location = loc
    cam.rotation_euler = rot
    scene.render.filepath = f"{prefix}_{name}.png"
    bpy.ops.render.render(write_still=True)
    print(f"wrote {scene.render.filepath}")

"""Blender-side converter: .blend -> .glb (run by convert_blend.ps1, or directly):

    blender -b model.blend --python tools/models/blend_to_glb.py -- output.glb [exclude_regex]

Exports only visible, renderable mesh geometry. Objects hidden in the viewport or render (rig widgets, reference
planes) and objects whose names match exclude_regex are deleted first. Modifiers (lattice, armature, subdivision)
are applied and external textures are embedded. glTF output is +Y up; Blender's -Y nose direction becomes glTF +Z,
which the entity catalog's modelRotationDeg [0, 180, 0] turns to Godot's -Z forward.
"""
import os
import re
import sys

import bpy

argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
if not argv:
    raise SystemExit("usage: blender -b in.blend --python blend_to_glb.py -- out.glb [exclude_regex]")
out = os.path.abspath(argv[0])
exclude = re.compile(argv[1]) if len(argv) > 1 and argv[1] else None
os.makedirs(os.path.dirname(out), exist_ok=True)

removed = []
for o in list(bpy.data.objects):
    hidden = not o.visible_get() or o.hide_render
    excluded = exclude is not None and exclude.search(o.name) is not None
    if o.type != "MESH" or hidden or excluded:
        # Keep non-mesh parents (armatures, lattices) that deform visible meshes; drop everything else.
        if o.type in {"ARMATURE", "LATTICE", "EMPTY"} and not excluded:
            continue
        removed.append(o.name)
        bpy.data.objects.remove(o, do_unlink=True)
print(f"removed {len(removed)} objects: {', '.join(removed[:30])}{' ...' if len(removed) > 30 else ''}")

for img in bpy.data.images:
    if img.source == "FILE" and not img.packed_file:
        path = bpy.path.abspath(img.filepath)
        if os.path.exists(path):
            try:
                img.pack()
            except RuntimeError as e:
                print(f"warning: could not pack {path}: {e}")
        else:
            print(f"warning: missing texture {img.filepath}")

meshes = [o for o in bpy.data.objects if o.type == "MESH"]
print(f"exporting {len(meshes)} mesh objects")
bpy.ops.export_scene.gltf(
    filepath=out,
    export_format="GLB",
    export_apply=True,
    export_cameras=False,
    export_lights=False,
)
print(f"wrote {out} ({os.path.getsize(out) / 1e6:.1f} MB)")

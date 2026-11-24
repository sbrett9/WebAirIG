"""Extracts one or more models from a .blend into normalized .glb files, driven by a JSON spec.

    blender -b --disable-autoexec --python tools/models/blend_extract_glb.py -- spec.json [only_out_substring]

Spec (paths relative to the repo root):
    {
      "source": "path/to/file.blend",
      "exports": [
        { "out": "models/aircraft/glb/f15.glb",
          "roots": ["^f_15$", "^Empty[.][0-9]+$"],   # regexes: hierarchy roots whose visible meshes are exported
          "exclude": "^Plane[.]003$",                # optional regex: objects to drop
          "lengthM": 19.43,                        # real length; the model is scaled so its nose-to-tail extent matches
          "maxTris": 150000,                       # optional: decimate (collapse) to roughly this many triangles
          "resetRootRotation": false,              # optional: zero the matched roots' rotation (undo a posed bank/pitch)
          "maxTextureSize": 512,                   # optional: downscale embedded textures (pseudo-IR needs little detail)
          "kind": "aircraft" }                     # "aircraft" (nose = end away from the tail fins) or "store"
      ]
    }

Every export is normalized the same way so the entity catalog needs no per-model fix-ups
(modelRotationDeg [0, 0, 0], modelScale 1, modelOffsetFrdM [0, 0, 0]):
  * Modifiers (mirror, edge split, armature pose) are applied and all parts are joined into one mesh.
  * Nose along Blender +Y, up along +Z. The glTF exporter turns that into nose -Z, up +Y: Godot's forward.
  * Bounding-box center at the origin. Uniform scale so the nose-to-tail length equals lengthM (meters).
The .blend is reopened for every export, so the exports cannot affect each other.
"""
import json
import math
import os
import re
import sys

import bmesh
import bpy
from mathutils import Matrix, Vector

argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
if not argv:
    raise SystemExit("usage: blender -b --disable-autoexec --python blend_extract_glb.py -- spec.json [only]")
REPO = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
spec_path = os.path.abspath(argv[0])
only = argv[1] if len(argv) > 1 else None
spec = json.load(open(spec_path, encoding="utf-8"))
source = os.path.join(REPO, spec["source"])


def root_of(o):
    while o.parent is not None:
        o = o.parent
    return o


def tri_count(me):
    return sum(len(p.vertices) - 2 for p in me.polygons)


def extract(job):
    bpy.ops.wm.open_mainfile(filepath=source, load_ui=False)
    roots = [re.compile(r) for r in job["roots"]]
    exclude = re.compile(job["exclude"]) if job.get("exclude") else None
    if job.get("resetRootRotation"):
        # Some authors pose the whole model (e.g. a banked render) by rotating a parent empty. Remove that pose
        # but keep the root's location and scale, which define the model's proportions.
        for o in bpy.data.objects:
            if o.parent is None and any(r.search(o.name) for r in roots):
                o.rotation_mode = "XYZ"
                o.rotation_euler = (0.0, 0.0, 0.0)
        bpy.context.view_layer.update()
    dg = bpy.context.evaluated_depsgraph_get()

    picked = [o for o in bpy.data.objects
              if o.type == "MESH" and o.visible_get() and not o.hide_render
              and any(r.search(root_of(o).name) for r in roots)
              and not (exclude and exclude.search(o.name))]
    if not picked:
        raise SystemExit(f"{job['out']}: no visible meshes under roots {job['roots']}")

    # Bake each part (modifiers applied, world transform) into one joined bmesh. Materials are remapped by name.
    joined = bpy.data.meshes.new("joined")
    bm = bmesh.new()
    mat_index = {}
    for o in picked:
        me = bpy.data.meshes.new_from_object(o.evaluated_get(dg), preserve_all_data_layers=True, depsgraph=dg)
        me.transform(o.matrix_world)
        if o.matrix_world.determinant() < 0:
            me.flip_normals()
        remap = []
        for slot_mat in me.materials:
            key = slot_mat.name if slot_mat else "__none__"
            if key not in mat_index:
                mat_index[key] = len(joined.materials)
                joined.materials.append(slot_mat)
            remap.append(mat_index[key])
        for p in me.polygons:
            p.material_index = remap[p.material_index] if remap else 0
        bm.from_mesh(me)
        bpy.data.meshes.remove(me)
    bm.to_mesh(joined)
    bm.free()
    obj = bpy.data.objects.new("model", joined)

    # Everything else goes: only the joined model is exported.
    for o in list(bpy.data.objects):
        if o is not obj:
            bpy.data.objects.remove(o, do_unlink=True)
    bpy.context.scene.collection.objects.link(obj)
    tris_in = tri_count(joined)

    max_tris = job.get("maxTris")
    if max_tris and tris_in > max_tris:
        mod = obj.modifiers.new("decimate", "DECIMATE")
        mod.decimate_type = "COLLAPSE"
        mod.ratio = max_tris / tris_in
        mod.use_collapse_triangulate = True
        dg = bpy.context.evaluated_depsgraph_get()
        decimated = bpy.data.meshes.new_from_object(obj.evaluated_get(dg), depsgraph=dg)
        obj.modifiers.clear()
        old = obj.data
        obj.data = decimated
        bpy.data.meshes.remove(old)

    me = obj.data
    pts = [v.co.copy() for v in me.vertices]
    lo = Vector((min(p.x for p in pts), min(p.y for p in pts), min(p.z for p in pts)))
    hi = Vector((max(p.x for p in pts), max(p.y for p in pts), max(p.z for p in pts)))
    ext = hi - lo

    # 1) Long axis to +Y. Aircraft are authored Z-up, so only X/Y can be the long axis; stores may lie along any axis.
    axes = [0, 1] if job["kind"] == "aircraft" else [0, 1, 2]
    long_axis = max(axes, key=lambda a: ext[a])
    if long_axis == 0:
        rot = Matrix.Rotation(math.radians(90), 4, "Z")
    elif long_axis == 2:
        rot = Matrix.Rotation(math.radians(-90), 4, "X")
    else:
        rot = Matrix.Identity(4)
    me.transform(rot)
    pts = [v.co.copy() for v in me.vertices]
    lo = Vector((min(p.x for p in pts), min(p.y for p in pts), min(p.z for p in pts)))
    hi = Vector((max(p.x for p in pts), max(p.y for p in pts), max(p.z for p in pts)))
    center = (lo + hi) / 2
    length = hi.y - lo.y

    # 2) Nose sign. Aircraft: the tail fins are the highest structure and sit at the rear.
    #    Stores: the tail end carries the fins, so it has the larger radial spread near the end.
    if job["kind"] == "aircraft":
        top = [p for p in pts if p.z > hi.z - 0.15 * (hi.z - lo.z)]
        tail_at_plus_y = sum(p.y for p in top) / len(top) > center.y
    else:
        def end_radius(sign):
            edge = hi.y if sign > 0 else lo.y
            sel = [p for p in pts if abs(p.y - edge) < 0.12 * length]
            return max(math.hypot(p.x - center.x, p.z - center.z) for p in sel)
        tail_at_plus_y = end_radius(+1) > end_radius(-1)
    if tail_at_plus_y:
        me.transform(Matrix.Rotation(math.pi, 4, "Z"))  # nose to +Y
        center = Vector((-center.x, -center.y, center.z))

    # 3) Center and scale to the real length.
    scale = job["lengthM"] / length
    me.transform(Matrix.Scale(scale, 4) @ Matrix.Translation(-center))
    me.update()
    pts = [v.co for v in me.vertices]
    size = Vector((max(p.x for p in pts) - min(p.x for p in pts), max(p.y for p in pts) - min(p.y for p in pts),
                   max(p.z for p in pts) - min(p.z for p in pts)))

    for img in bpy.data.images:
        if img.source == "FILE" and not img.packed_file and os.path.exists(bpy.path.abspath(img.filepath)):
            try:
                img.pack()
            except RuntimeError:
                pass
    max_tex = job.get("maxTextureSize")
    if max_tex:
        for img in bpy.data.images:
            w, h = img.size[:]
            if img.users and max(w, h) > max_tex:
                f = max_tex / max(w, h)
                img.scale(max(1, int(w * f)), max(1, int(h * f)))
                # scale() changes the pixels but keeps the old packed bytes; repack so the exporter (which may copy
                # packed bytes verbatim) writes data that matches the image's size and declared format. Repack as PNG:
                # Blender's re-encoded JPEGs carry an ICC (APP2) segment that Godot's libjpeg-turbo loader rejects.
                # The glTF exporter picks the MIME type from the file path's extension, so rename the path too.
                img.file_format = "PNG"
                img.filepath_raw = "//" + re.sub(r"[^A-Za-z0-9_-]+", "_", os.path.splitext(img.name)[0]) + ".png"
                img.pack()

    out = os.path.join(REPO, job["out"])
    os.makedirs(os.path.dirname(out), exist_ok=True)
    obj.select_set(True)
    bpy.context.view_layer.objects.active = obj
    bpy.ops.export_scene.gltf(filepath=out, export_format="GLB", use_selection=True, export_apply=True,
                              export_cameras=False, export_lights=False, export_yup=True)
    print(f"@@WROTE {job['out']}: parts {len(picked)}, tris {tris_in} -> {tri_count(me)}, "
          f"scale x{scale:.3f}, nose was {'+' if not tail_at_plus_y else '-'}Y, "
          f"size fwd {size.y:.2f} x span {size.x:.2f} x height {size.z:.2f} m, {os.path.getsize(out) / 1e6:.1f} MB")


for job in spec["exports"]:
    if only and only not in job["out"]:
        continue
    extract(job)

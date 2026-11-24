# Models

## Aircraft and target models: `models/aircraft/`

The IG loads `.glb`, `.gltf`, `.fbx` and `.obj` (with `.mtl` and textures) at runtime, with no Godot import step.
`.blend` files must be converted first. Install Blender, then run the converter, which writes
`models/aircraft/glb/<name>.glb`:

```powershell
winget install --id BlenderFoundation.Blender -e --source winget
./tools/models/convert_blend.ps1
```

To use a model, put the file here and point its catalog entry at it in `config/entity_catalog.json`. The path is
relative to the `config` folder:

```json
{ "typeId": 1, "name": "B777", "model": "../models/aircraft/b777.glb", "modelScale": 1.0, "modelRotationDeg": [0, 180, 0], ... }
```

Each entry has three settings that adjust the model's pose:

| Setting | Purpose | Notes |
|---|---|---|
| `modelRotationDeg` | Turns the model so the nose points along Godot -Z with +Y up. | glTF models usually face +Z, hence the default `[0, 180, 0]`. |
| `modelScale` | Scales the model to meters. | Use `0.01` if the source was authored in centimeters. |
| `modelOffsetFrdM` | Moves the model so the entity origin sits at the center of gravity. | Body axes in meters: forward, right, down. |

When a model loads, the IG log prints its extent, for example
`extent fwd 19.5 m x span 31.7 m x height 5.7 m (nose at +6.2 m)`. A small `fwd` with a large `span` usually means
the model is rotated 90°.

Model licenses are listed in [aircraft/CREDITS.md](aircraft/CREDITS.md).

After loading a model, check these:
- **IR hotspots** (`ir.hotspots`, body FRD meters): place them at the engines and exhausts.
- **Camera mounts** in `config/sensors.json`: fore cameras on the lateral lines, aft cameras under the
  horizontal stabilizers. The aft mounts must sit outside the stabilizer mesh.
- **Visual check:** run the IG with `-- --snapshot-frame 90 --snapshot-dir output/snapshots --no-stream` and
  look at the `*_truth.png` files. Truth boxes come from the model's own vertices, so a box that does not hug
  the aircraft means the rotation, scale or offset is wrong.

## Model tools (`tools/models/`, run with Blender in background mode)

| Script | Purpose |
|---|---|
| `inspect_model.py` | Lists objects, world sizes, sizes after modifiers, materials, and missing textures. |
| `preview_views.py` | Renders top and side orthographic previews. A red cube marks the origin, which shows nose direction, scale and origin height at a glance. |
| `section_probe.py` | Reports vertex extents inside a slab, used to measure fuselage centerlines, stabilizers and engines for mounts and hotspots. |
| `blend_to_glb.py` / `convert_blend.ps1` | Convert `.blend` to `.glb`. Hidden helpers and name-matched junk are dropped. |
| `fbx_split_to_glb.py` | Splits a multi-model FBX (such as the weapon pack) into one centered `.glb` per model. |

Example:

```powershell
& "C:\Program Files\Blender Foundation\Blender 5.3\blender.exe" -b model.blend --python tools/models/preview_views.py -- preview/model
```

## Current models

| Type | Model | Converted file | Notes |
|---|---|---|---|
| 1 B777 (ownship) | `United Airlines Boeing 777.blend` | `aircraft/glb/b777_united.glb` | scale 5.7, origin moved to the fuselage centerline |
| 41 UH60 | `Uh-60 BlackHawk.zip` | `aircraft/glb/uh60.glb` | scale 1.17, original textures missing |
| 11 F15 | `F15 EAGLE.zip` (BlendSwap #84483, CC BY-NC) | `aircraft/glb/f15.glb` | `blend_extract_glb.py` + `specs/f15.json`: decimated to 150k triangles, normalized (no catalog fix-ups) |
| 12 SU27 | `SU_27 FLANKER.zip` (BlendSwap #88864, CC BY-NC) | `aircraft/glb/su27.glb` | `blend_extract_glb.py` + `specs/su27.json`: decimated to 150k triangles, normalized |
| 13 F22 | `F-22 Raptor.blend` (BlendSwap #30247, CC BY) | `aircraft/glb/f22.glb` | `blend_extract_glb.py` + `specs/f22.json`: posed roll removed (`resetRootRotation`), textures downscaled to 512 px (`maxTextureSize`), normalized |
| 60 C130 | `C-130 Transport.zip` | `aircraft/glb/c130.glb` | scale 4.1, origin moved to the fuselage center |
| 70–79 missiles and bombs | `Aircraft payload pack.zip` (BlendSwap #38676, CC BY 3.0) | `munitions/glb/*.glb` | `blend_extract_glb.py` + `specs/payload_pack.json`: one file per weapon, real length, normalized. AIM-7, AGM-88 and GBU-57 have no model and use the procedural missile/bomb placeholder. |

**Normalizing extractor.** `tools/models/blend_extract_glb.py` takes a JSON spec and produces `.glb` files that need
no catalog fix-ups:
- visible meshes only, with modifiers applied
- optional decimation
- nose detected and pointed forward
- centered and scaled to the real length

To run it:

```powershell
& "C:\Program Files\Blender Foundation\Blender 5.3lender.exe" -b --disable-autoexec --python tools/models/blend_extract_glb.py -- tools/models/specs/payload_pack.json
```

The specs expect the downloaded archives unpacked under `models/aircraft/src/` or `models/munitions/src/`.
Those folders are git-ignored.

## YOLO models: `models/yolo/`

The consumer loads ONNX files from this folder. The default path is `models/yolo/yolo26n.onnx`. The weights
are AGPL-3.0 (Ultralytics) and are not committed. [yolo/README.md](yolo/README.md) shows how to fetch and export
them.

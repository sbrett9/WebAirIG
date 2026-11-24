# Third-party notices

WebAirIG's own source code, configuration and documentation are licensed under the [MIT License](LICENSE).

This file lists third-party material that is included in this repository, or that WebAirIG uses without including
it. **Third-party material keeps its own license. The MIT License does not cover it.**

## Included in this repository

### 3D models (`models/`)

Each model is credited as its Creative Commons license requires: title, author, source, license, and the changes
made for WebAirIG.

| Model | Files in this repo | Title, author, source | License | Changes |
|---|---|---|---|---|
| Boeing 777 (ownship) | `models/aircraft/United Airlines Boeing 777.blend`, `models/aircraft/glb/b777_united.glb`, `models/aircraft/preview/b777_*.png` | "United Airlines Boeing 777" by **CFUdd**, [BlendSwap #30085](https://blendswap.com/blend/30085) | Creative Commons Attribution (CC BY), as stated on the BlendSwap listing | Converted to glTF binary with Blender, keeping only visible meshes. Preview renders added. |
| C-130 | `models/aircraft/glb/c130.glb`, `models/aircraft/preview/c130_*.png` | "C-130 Transport" by **hjmediastudios**, [BlendSwap #67155](http://www.blendswap.com/blends/view/67155) | [CC BY 3.0](https://creativecommons.org/licenses/by/3.0/) | Converted to glTF binary with Blender, keeping only visible meshes. Preview renders added. |
| UH-60 Black Hawk | `models/aircraft/glb/uh60.glb`, `models/aircraft/preview/uh60_*.png` | "Uh-60 BlackHawk" by **PapaDragon**, BlendSwap #69687 | [CC BY-SA 3.0](https://creativecommons.org/licenses/by-sa/3.0/) | Converted to glTF binary with Blender, keeping only visible meshes. Preview renders added. **These files remain under CC BY-SA 3.0**, and so must any further changes to them. |
| F-15 Eagle | `models/aircraft/glb/f15.glb` | "F15 EAGLE" by **shontoloyo**, [BlendSwap #84483](https://www.blendswap.com/blends/view/84483) | [CC BY-NC 3.0](https://creativecommons.org/licenses/by-nc/3.0/), **NonCommercial** | Visible meshes joined with modifiers applied, decimated from about 530k to 150k triangles, scaled to the real 19.43 m length, centered, and exported to glTF binary (`tools/models/specs/f15.json`). |
| Su-27 Flanker | `models/aircraft/glb/su27.glb` | "SU_27 FLANKER" by **shontoloyo**, [BlendSwap #88864](https://www.blendswap.com/blends/view/88864) | [CC BY-NC 3.0](https://creativecommons.org/licenses/by-nc/3.0/), **NonCommercial** | Visible meshes joined with modifiers applied, decimated from about 700k to 150k triangles, scaled to the real 21.94 m length, centered, and exported to glTF binary (`tools/models/specs/su27.json`). |
| F-22 Raptor | `models/aircraft/glb/f22.glb` | "F-22 Raptor" by **FA1RYDUST**, [BlendSwap #30247](https://blendswap.com/blend/30247). Its metal textures are ambientCG Metal037/Metal040 ([CC0](https://docs.ambientcg.com/license/)). | Creative Commons Attribution (CC BY), as stated on the BlendSwap listing | The author's posed 16° roll removed, parts joined with modifiers applied, scaled to the real 18.92 m length, centered, textures downscaled to 512 px, and exported to glTF binary (`tools/models/specs/f22.json`). |
| Aircraft payload pack: AIM-9X, AIM-120, AIM-54, AGM-65, AGM-84H, GBU-16, Mk 84 | `models/munitions/glb/*.glb` | "Aircraft payload pack" by **manfro1981**, [BlendSwap #38676](http://www.blendswap.com/blends/view/38676) | [CC BY 3.0](https://creativecommons.org/licenses/by/3.0/) | One glTF binary per weapon: parts joined, nose pointed forward, centered, and scaled to each weapon's real length (`tools/models/specs/payload_pack.json`). |

**NonCommercial models.** The F-15 and Su-27 are licensed for **non-commercial use only**. WebAirIG is a free,
non-commercial project, so it can include them. Anyone reusing this repository commercially must remove or replace
those two files: `config/entity_catalog.json` falls back to procedural placeholders when a model file is missing. The
MIT License on the code does not change their terms.

**Trademarks.** The 777 model carries the United Airlines livery. United Airlines and its logo are trademarks of
United Airlines, Inc. Aircraft and weapon names are used only to describe the models. Nothing here implies
endorsement by any trademark owner.

**Rendered imagery and datasets.** Images, videos and YOLO datasets rendered from these models depict
third-party works. When you share them, keep the attribution above with them. Treat imagery of the UH-60 as
subject to CC BY-SA 3.0, and imagery of the F-15 and Su-27 as non-commercial.

**Not included.** A git-ignored `models/local/` folder can hold private models that have no redistributable
license. Nothing in the repository depends on it. Catalog types whose model file is missing render as procedural
placeholders.

### three.js (`src/WebAirIG.Host/wwwroot/lib/three/`)

three.js r170 is © 2010-2024 three.js authors and licensed under the [MIT License](src/WebAirIG.Host/wwwroot/lib/three/LICENSE).
It is copied from the npm package `three@0.170.0`. The add-on modules have their `three` import rewritten to a
relative path.

## Used but not included

These are downloaded at build time or installed separately. They are not part of this repository.

| Component | License | How WebAirIG uses it |
|---|---|---|
| [Godot Engine](https://godotengine.org) 4.7.2 (.NET), `Godot.NET.Sdk`, `GodotSharp` | MIT | Runs the image generator. NuGet supplies the SDK packages. |
| [Radzen.Blazor](https://github.com/radzenhq/radzen-blazor) | MIT | Web UI components |
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) | MIT | MVVM base classes |
| [ONNX Runtime (DirectML)](https://github.com/microsoft/onnxruntime) | MIT | Runs YOLO inference in the consumer |
| [SkiaSharp](https://github.com/mono/SkiaSharp) | MIT | Preview rendering in the consumer |
| [xUnit.net](https://github.com/xunit/xunit), Microsoft.NET.Test.Sdk | Apache-2.0, MIT | Unit tests |
| [FFmpeg](https://ffmpeg.org) | LGPL/GPL, depending on the build | Run as a separate program for NVENC encoding and NVDEC decoding. It is not bundled. If you redistribute FFmpeg binaries with WebAirIG, their license terms apply to that distribution. |
| [Ultralytics](https://github.com/ultralytics/ultralytics) (Python package) and the **YOLO26 weights** | **AGPL-3.0**, with commercial licenses available from Ultralytics | The scripts in `tools/yolo/` import it to export and train models. The weights (`yolo26n.pt`, and the `yolo26n.onnx` exported from it) are **not** in this repository; see `models/yolo/README.md` to fetch them. WebAirIG's MIT license does not change their AGPL terms. Review them, or get an Ultralytics license, before using these models in a product or network service. |

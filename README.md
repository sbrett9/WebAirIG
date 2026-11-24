# WebAirIG

WebAirIG is a simple image generator that renders pseudo-MWIR imagery for testing computer vision. Four 2-axis
gimbaled cameras ride on a 777 ownship flying figure-8s, and each one renders a jet-colored 2048×2048 view of the
same world.

- **Video:** each view is NVENC-encoded and streamed over UDP or TCP. Every frame carries its own ground-truth
  sidecar.
- **Consumer:** decodes the streams, runs YOLO26, scores the detections against truth, and records labeled
  training sets.
- **Control and output:** the IG is driven over CIGI 4.0, and the host publishes DIS Entity State PDUs.

```
 ┌──────────────── WebAirIG.Host (Blazor + Radzen, :5080) ───────────────┐
 │ scenario sim: ownship figure-8, targets on cyclic patterns            │──DIS ESPDU (UDP :3000)──▶
 │ sensor control: gimbals, FOV, IR gain/level, coverage map             │
 │ CIGI 4.0 host  ◀── Start of Frame ── / ── IG Ctrl, Entity Ctrl/Pos, ──▶│
 └──────────────────────────────────── Art Part, View Ctrl/Def, Sensor ──┘
                          UDP :8005 ▲               │ UDP :8004
                                    │               ▼
 ┌──────────────── WebAirIG IG (Godot 4.7.2 .NET, ig/) ──────────────────┐
 │ entity loader (glb/gltf/fbx or placeholders) + pseudo-IR shaders       │
 │ 4 × gimbaled cameras → HDR temperature render → sensor post (jet)     │
 │ per-frame truth (tight boxes from model vertices) → async GPU readback │
 │ ffmpeg NVENC (h264/hevc) per camera → frame message + truth           │
 └────────────────────────────── UDP or TCP :5600 ───────────────────────┘
                                    ▼
 ┌──────────────── WebAirIG.Consumer (Blazor + Radzen, :5090) ───────────┐
 │ reassembly → ffmpeg NVDEC → YOLO26 (ONNX Runtime, DirectML) → tracker │
 │ truth vs detection metrics · MJPEG previews · YOLO dataset recorder   │
 └────────────────────────────────────────────────────────────────────────┘
```

## Quick start

### Prerequisites

**Required**

| What | Why | Install |
|---|---|---|
| Windows 10/11 x64 | The streaming path uses Windows NVENC/NVDEC and DirectML | — |
| NVIDIA GPU with a current driver | NVENC encode in the IG, NVDEC decode in the consumer, Vulkan for Godot | [nvidia.com/drivers](https://www.nvidia.com/Download/index.aspx) |
| Git | Clone the repo | `winget install --id Git.Git -e` |
| .NET 10 SDK | Builds the host, consumer, IG scripts and tests | `winget install --id Microsoft.DotNet.SDK.10 -e` |
| Godot 4.7.2 **.NET** build | Runs the IG. It is not in the repo (git-ignored). | Download `Godot_v4.7.2-stable_mono_win64.zip` from [godotengine.org/download/archive](https://godotengine.org/download/archive/) |
| ffmpeg with NVENC | Video encode and decode | `winget install --id Gyan.FFmpeg -e --source winget` |
| PowerShell 7 | Runs `scripts/run-all.ps1`. Windows PowerShell 5.1 blocks scripts by default. | `winget install --id Microsoft.PowerShell -e` |
| A WebGL2 browser (Edge, Chrome, Firefox) | The host and consumer UIs, including the 3D coverage view | — |

WebAirIG looks for ffmpeg in `WEBAIRIG_FFMPEG`, the winget install folders, `tools/ffmpeg/bin`, then `PATH`.

**Optional**

| What | Needed for | Install |
|---|---|---|
| Python 3.12+ with Ultralytics | Fetching and exporting the YOLO26 weights the consumer's detector uses (not in the repo, AGPL-3.0), and training your own | See [models/yolo/README.md](models/yolo/README.md) |
| Blender 5.x | Converting `.blend`/`.fbx` sources to `.glb` (`tools/models/`) | `winget install --id BlenderFoundation.Blender -e` |

**In the repo:** the runtime models in `models/*/glb`, with textures embedded. They are third-party Creative
Commons works; see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). The original download archives are not
committed. Catalog types without a model file render as procedural placeholders.

**Not in the repo:** the YOLO26 weights. They are AGPL-3.0, from Ultralytics.
[models/yolo/README.md](models/yolo/README.md) fetches and exports them into `models/yolo/`, where the consumer
looks. Without them, everything runs except detection.

### First-time setup

```powershell
git clone https://github.com/sbrett9/WebAirIG.git
cd WebAirIG
# Unzip Godot_v4.7.2-stable_mono_win64.zip here, so this path exists:
#   .\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64.exe
dotnet build WebAirIG.slnx
# One-time import of the Godot project (creates ig/.godot):
.\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64_console.exe --headless --path ig --import
```

For YOLO detection in the consumer, also fetch the YOLO26n weights once; see
[models/yolo/README.md](models/yolo/README.md).

NuGet restores Godot's .NET packages from nuget.org, so the first build needs internet access.

### Run

From PowerShell 7 at the repo root:
```powershell
./scripts/run-all.ps1 -Build
```
This builds the solution and starts three things:
- the **host** at http://localhost:5080
- the **consumer** at http://localhost:5090
- the **IG** window, which shows the 2×2 sensor grid

Add `-NoIg`, `-NoHost` or `-NoConsumer` to skip any of them.

In the IG window:
- `T` toggles the truth boxes.
- `1`–`4` shows a single camera, and `0` returns to the grid.

**Without the web UIs**, from the repo root:
```powershell
Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64.exe --path ig -- --no-stream --snapshot-frame 90 --snapshot-dir output/snapshots --quit-after-snapshot
```
This saves a PNG per camera, an annotated `_truth.png`, and the truth JSON.

## What runs where

| Component | Tech | Role |
|---|---|---|
| `src/WebAirIG.Host` | .NET 10 Blazor Server, Radzen, strict MVVM + DI | Runs the scenario (ownship and targets). It is the CIGI 4.0 host, the DIS publisher, and the operator UI for gimbals, FOV, IR settings, the coverage map and the scenario. It also has a REST API for scripting. |
| `ig/` | Godot 4.7.2 .NET (C#) | The CIGI IG. Renders pseudo-MWIR, builds per-frame truth, and encodes with NVENC through ffmpeg. |
| `src/WebAirIG.Consumer` | .NET 10 Blazor Server, Radzen, strict MVVM + DI | Receives and decodes the streams, runs YOLO26 and the tracker, computes metrics, records datasets, and serves previews. |
| `src/WebAirIG.Common` | net8.0 library | Shared by every component: geodesy (WGS-84, local tangent plane, frames), gimbal math, truth schema, video transport, Annex-B splitter, ffmpeg wrappers, flight patterns. |
| `src/WebAirIG.Cigi` | net8.0 library | CIGI 4.0 codec for the packet subset WebAirIG uses. |
| `src/WebAirIG.Dis` | net8.0 library | DIS 7 Entity State PDU, and ECEF and Euler-angle conversions. |
| `src/WebAirIG.Web.Mvvm` | Razor class library | Base classes for ViewModel-bound pages and controls and for polling ViewModels. |
| `tests/WebAirIG.Tests` | xUnit | Covers geodesy, gimbals, CIGI byte orders, DIS, fragmentation, Annex-B splitting and flight patterns. |

The shared libraries target `net8.0` because Godot 4.7 C# projects target `net8.0`.

## Key behaviors

- **Frame-locked simulation.** In CIGI sync mode, each frame the IG sends Start of Frame and waits for the
  host's reply for that same frame. Sim time therefore advances exactly one IG frame period per rendered frame,
  even if rendering runs slower than real time, so each frame and its truth are deterministic. With no IG
  connected, the host free-runs.
- **Gimbals.** The rig in `config/sensors.json` is lateral. Two cameras sit on the fuselage sides and two on the
  horizontal stabilizer tips. At (0,0) each camera looks horizontally out its own side, with the image upright:
  - elevation: ±23.2°, positive up
  - azimuth: ±46.4°, positive clockwise seen from above (toward the nose on left cameras, toward the tail on right
    cameras)

  Ownship structure enters the frame when the cameras slew fore or aft: wings, engines and wingtips. A mount with
  pitch −90° gives a nadir camera instead.

  The host drives the gimbals through CIGI Articulated Part Control (part ID = 100 + view ID). Camera modes:
  - Center: gimbal (0,0)
  - Nadir: as close to straight down as the limits allow
  - Manual
  - Track entity: keeps a target centered, within limits
  - Geo point: click the 2D coverage map
  - Scan

  The Fwd, Aft, Up, Down, Left and Right buttons point toward aircraft directions for any mount. When a tracked
  target is outside the gimbal limits, the camera card shows the requested az/el against the limits; otherwise it
  shows the target's range and expected size in pixels.
- **3D coverage view.** The Sensors page opens on a three.js view (vendored under
  `src/WebAirIG.Host/wwwroot/lib/three`, so it works offline). It loads the same entity models as the IG with the
  same orientation, scale and offset fix-ups:
  - **Ownship mode:** the 777 at true scale, the four camera mounts, live frustums of adjustable length, and each
    camera's field of regard (every boresight direction the gimbal can reach)
  - **Tactical mode:** all traffic with enlarged symbols, long frustums, ground footprints, and lines of sight
    from tracking cameras (dashed red when the target is out of reach)
  - **Selecting an aircraft:** click it to see, per camera, whether it is within reach (with az/el and the
    expected pixel size), then assign a camera to track it

  The 2D top-down map is still available from the 3D view / 2D map switch.
- **Pseudo-MWIR.** Scene shaders output apparent temperature: skin temperature, solar loading, model-texture
  detail, engine and exhaust hotspots, atmospheric path attenuation, a cold sky, and procedural fields, roads,
  urban areas and water. A per-sensor post-process adds optics blur, fixed-pattern and temporal noise,
  gain/level, polarity and the jet palette. Gain, level, noise, polarity and on/off come from CIGI Sensor
  Control.
- **Truth sidecar.** The truth JSON travels inside every video frame message, so it can never drift out of sync
  with the image. It can also be logged to JSONL on disk. Truth boxes are tight: they come from projecting
  sampled model vertices with the same double-precision camera pose used to place the Godot camera. See
  [docs/ICD.md](docs/ICD.md#4-truth-sidecar).
- **DIS.** The host sends DIS 7 Entity State PDUs: ECEF position, ECEF Euler angles, velocity, dead reckoning
  model 2 and markings. By default they go to `127.0.0.1:3000`. Set `WebAirIG:Dis:Address` to
  `255.255.255.255` or a multicast group to put them on a network.

## Configuration

| File | Used by | Contents |
|---|---|---|
| `config/world.json` | host + IG | Geodetic origin, procedural terrain, atmosphere and sun. Host and IG must share this file. |
| `config/entity_catalog.json` | host + IG + consumer | Entity types: model file, orientation fix, IR signature and hotspots, YOLO class, DIS type, default speed. |
| `config/sensors.json` | host + IG | The 4-camera rig: mounts, gimbal limits, slew rate, resolution, FOV range. |
| `config/scenario.json` | host | Ownship pattern and targets. The host's Scenario page edits it. |
| `ig/config/ig.json` | IG | CIGI ports, sync mode, fps, MSAA, stream settings (protocol, destination, codec, bitrate, GOP), truth logging. |
| `src/*/appsettings.json` | host / consumer | Network ports, DIS, receiver, decoder, detector, recorder. |

**Flight patterns.** Circle, Ellipse, Racetrack, Figure8 (lemniscate, with a smooth roll reversal),
Box, Lawnmower, OutAndBack, Weave, Hold, and Formation (holds an offset from ownship, in ownship body axes).
Formation can add a slow Lissajous "wander" around the offset (`formationWander*`), so a target drifts in and
out of a camera's frame instead of sitting still in it. Each pattern also takes an optional altitude
oscillation and a bank factor.

The default scenario is tuned for the lateral rig. The gimbals see from 23.2° above to 23.2° below the
aircraft's horizontal (30.7° with half the FOV), so the formation targets ESCORT and RAPTOR1 sit about 1 km out to
the side and only 80–120 m below ownship. Traffic flies near ownship's altitude. QUAD1, HELO1 and HAWK1 stay
low as hard, long-range cases.

**Your models.** Drop them in `models/aircraft/` and see [models/README.md](models/README.md). Until then the IG
draws procedural placeholders.

## Ports

| Port | Protocol | Purpose |
|---|---|---|
| 8004 | UDP | CIGI host → IG |
| 8005 | UDP | CIGI IG → host |
| 5600 | UDP or TCP | Video + truth, IG → consumer. For TCP the consumer listens and the IG connects. |
| 5610+cam | UDP | Optional MPEG-TS mirror for VLC/ffplay (`stream.mirrorUrlTemplate`) |
| 3000 | UDP | DIS |
| 5080 | HTTP | Host UI and `/api` |
| 5090 | HTTP | Consumer UI, `/preview/stream` (MJPEG) and `/preview/snapshot.jpg` |

**Host REST API**, for scripted tests:
- `GET /api/state`
- `POST /api/cameras/{id}` with a body such as `{"mode":"TrackEntity","trackEntityId":6,"fovDeg":10}`
- `POST /api/sim/pause`, `/api/sim/resume`, `/api/sim/reset`

## Terrain and geolocation (Cesium)

There is no official Cesium plugin for Godot. As of November 2026, two community options exist:

| Plugin | Version and support | Main limitation |
|---|---|---|
| `Battle-Road-Labs/3D-Tiles-For-Godot` | v1.0.1 (Aug 2025) | No C# bindings. |
| `brynnb/cesium-godot` | v0.3.2 (Sep 2026). Tested on Godot 4.6.3–4.7.2; ships C# wrappers, `CesiumGeoreference`, `CesiumGlobeAnchor` and 3D tilesets. | Pre-1.0. |

WebAirIG v1 renders a flat procedural world on a local tangent plane, with exact WGS-84 conversions at the CIGI,
DIS and truth boundaries. Lat/lon/alt are therefore correct everywhere; only the rendered ground ignores earth
curvature (about 31 m of drop at 20 km). Terrain is selected by `world.json` `terrain.provider`, which leaves room
for a future `"cesium"` provider. That provider would anchor `CesiumGeoreference` at the same origin and replace
the ground plane with Cesium World Terrain.

## Troubleshooting

- **The IG says "ffmpeg not found".** Install it as described under Prerequisites, or set `stream.ffmpegPath` in
  `ig/config/ig.json`.
- **The consumer shows no video.** Check that the IG HUD shows `enc N/N` and `tx` increasing. On UDP, check the
  consumer's "incomplete" counter. On a lossy LAN, lower `stream.bitrateKbps`, set `stream.pacingMbps`, or
  switch both ends to `tcp`.
- **Truth boxes don't fit your model.** Fix `modelRotationDeg`, `modelScale` and `modelOffsetFrdM` in the catalog
  and check the snapshot's `_truth.png`.

## License

WebAirIG's code, configuration and documentation are released under the [MIT License](LICENSE).

Third-party material keeps its own license:
- **3D models in `models/`:** CC BY, CC BY-SA 3.0 and CC BY-NC 3.0. They all require attribution.
  - The UH-60 is ShareAlike.
  - **The F-15 and Su-27 are NonCommercial:** remove or replace them before any commercial use.
- **three.js:** MIT.
- **Ultralytics YOLO26 weights and Python package:** AGPL-3.0. They are not included.

See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) for the authors, sources and changes made.

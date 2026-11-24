# WebAirIG Interface Control Document

This document specifies every external interface of WebAirIG: the CIGI 4.0 subset, the coordinate frames, the
video transport and the truth sidecar.

## 1. Coordinate frames

| Frame | Axes | Used for |
|---|---|---|
| Geodetic | WGS-84 latitude and longitude (deg), height above the ellipsoid (m) | CIGI Entity Position, truth, and the input to the DIS conversion |
| ECEF | WGS-84 Earth-centered, Earth-fixed | DIS location, velocity and orientation |
| World LTP (ENU / NED) | Local tangent plane at `world.json` `origin`; ENU x = east, y = north, z = up | Host simulation, IG world. The ground plane is at z = 0, which is the origin's altitude. |
| Body FRD | x forward, y right, z down | Entities, mounts, hotspots, model offsets |
| Camera C | x = boresight, y = image right, z = image down | Projection and gimbal math |
| Godot | +X east, +Y up, +Z south; node-local -Z = forward | Rendering only |

**Attitude.** On the wire, heading/pitch/roll are relative to local NED at the entity's own position. The IG
converts them to the LTP frame and corrects for meridian convergence (`LocalTangentPlane.LtpNedFromBody`).

**Gimbal.** The camera frame is built as
`R_body←cam = R_body←mount(yaw, pitch, roll) · Rz(az) · Ry(el)`. At gimbal (0,0) the boresight is the mount's
x axis. The limits are in `sensors.json`; the host clamps commands to them, and so does the IG.

| Mount | Setting | At (0,0) | +az | +el |
|---|---|---|---|---|
| Lateral (default rig) | yaw −90° left / +90° right, pitch 0 | Horizontal out the side, horizon level. Left cameras have the nose on image right; right cameras have the tail on image right. | Clockwise seen from above: toward the nose on the left, toward the tail on the right | Up |
| Nadir | pitch −90° | Straight down, image top toward the nose | Toward the right wing | Toward the nose |

**Ownship truth.** When ownship surrounds the camera (lens within centimeters of the skin), its `bbox` comes from
the projected sample points in front of the camera and can be much larger than the visible part. Ownship is
excluded from scoring and dataset labels by default.

**Projection** (pinhole, square pixels, symmetric FOV):
- `u = cx + fx·(y_c / x_c)`
- `v = cy + fy·(z_c / x_c)`
- `fx = fy = (H/2) / tan(vfov/2)`
- `cx = W/2`, `cy = H/2`
- Pixel (0,0) is the top-left corner of the image, so pixel centers sit at +0.5.

## 2. CIGI 4.0 subset

Every packet starts with `u16 Packet Size, u16 Packet ID`. Each message is one UDP datagram, and its first packet
is IG Control (host to IG) or Start of Frame (IG to host). There is no byte-swap magic number in 4.0: if the first
byte of a message is `0x00`, the message is big-endian. Both directions accept either byte order. WebAirIG sends
little-endian by default; set `BigEndian` / `bigEndian` in config to change it.

Packet layouts were checked against Wireshark's `packet-cigi.c` and CIGI Class Library 4.0 (`Cigi*V4.cpp`). The
SISO standard text itself was not available.

### Host → IG

| Packet | ID | Size | Sent | WebAirIG usage |
|---|---|---|---|---|
| IG Control | 0x0000 | 24 | every message | Mode Operate. Host frame number. Last IG frame number = the SOF being answered. Timestamp = host sim time (10 µs ticks). |
| Entity Control | 0x0025 | 16 | on create, every 30 frames, and on scenario change | Active, with the short entity type (offset 10) = catalog `typeId`. Remove is sent 3 times when an entity is deleted. Entity 0 is ownship. |
| Entity Position | 0x0001 | 48 | every message, for all entities | Top level. Lat/lon/alt (f64). Yaw/pitch/roll (f32) relative to local NED. |
| Articulated Part Control | 0x0005 | 32 | every message | Gimbals on entity 0: part ID = 100 + view ID, yaw = azimuth, pitch = elevation. |
| View Control | 0x000F | 40 | every 30 frames and on change | View ID = camera ID, attached to entity 0. x/y/z = mount offset (FRD, m); roll/pitch/yaw = mount orientation. |
| View Definition | 0x0014 | 40 | every 30 frames and on change | Left/right/top/bottom half-angles (deg). The IG uses top − bottom as the vertical FOV. Near/far in m. |
| Sensor Control | 0x0010 | 32 | every 30 frames and on change | View ID, sensor on, polarity (black-hot), gain, level (0..1), noise (sigma). |

### IG → Host

| Packet | ID | Size | Usage |
|---|---|---|---|
| Start of Frame | 0xFFFF | 24 | IG frame number. Timestamp = frame / fps (10 µs ticks). Last host frame. The Overframing flag is set when a frame took more than 1.5 periods. |

**Synchronization.** In sync mode (`ig.json cigi.syncMode`) the sequence is:
1. The IG sends Start of Frame for frame N.
2. The IG waits up to `syncTimeoutMs` for an IG Control whose `LastIgFrameNumber` is N.
3. The IG applies that message and renders frame N.

The host advances sim time by the IG timestamp delta on every SOF. If 15 waits in a row time out, the IG stops
waiting until the host comes back.

## 3. Video transport

There is one encoder per camera: ffmpeg with `h264_nvenc` or `hevc_nvenc`, B-frames off, AUD on, GOP set by
`stream.gop`. Each access unit becomes one **frame message**. All camera streams share one destination, and the
camera ID in the header tells them apart.

### Frame message (little-endian)

| Offset | Size | Field |
|---|---|---|
| 0 | 4 | magic `"WAIG"` |
| 4 | 1 | version = 1 |
| 5 | 1 | codec: 1 = H.264, 2 = HEVC |
| 6 | 1 | camera ID |
| 7 | 1 | flags: bit 0 = keyframe |
| 8 | 8 | IG frame number (the same for all cameras rendered in one frame) |
| 16 | 8 | sim time (double, s) |
| 24 | 8 | UTC capture time (.NET ticks) |
| 32 | 2 | width |
| 34 | 2 | height |
| 36 | 4 | payload length |
| 40 | 4 | truth length |
| 44 | 4 | session ID (random per IG run) |
| 48 | 4 | per-camera sequence number (+1 per message sent; a gap means a message was lost) |
| 52 | 12 | reserved |
| 64 | n | payload: one Annex-B access unit |
| 64+n | m | truth: UTF-8 JSON (section 4) |

### Framing on the wire

**TCP.** Each message is `[u32 LE length][frame message]`. The consumer listens and the IG connects, reconnecting
as needed.

**UDP.** Each message is split into datagrams of at most `stream.maxDatagramBytes` bytes (default 1400). Each
datagram starts with a 32-byte fragment header:

| Offset | Size | Field |
|---|---|---|
| 0 | 4 | `"WAIF"` |
| 4 | 1 | version |
| 5 | 1 | camera ID |
| 6 | 2 | fragment index |
| 8 | 2 | fragment count |
| 10 | 2 | reserved |
| 12 | 4 | session ID |
| 16 | 8 | frame number |
| 24 | 4 | total message length |
| 28 | 4 | byte offset of this fragment |

Incomplete messages are dropped after 750 ms.

### Consumer resync

When the consumer sees a sequence gap, a session or format change, or a decoder backlog, it drops frames until
the next keyframe. This keeps decoder output 1:1 with access units, which is how each decoded frame stays paired
with its header and truth.

## 4. Truth sidecar

The truth JSON uses schema `webairig.truth/1`, with one object per camera per frame. It travels inside every
frame message. If `truth.logToDisk` is set, it is also written to `output/truth/session_XXXXXXXX/camN.jsonl`.

```jsonc
{
  "schema": "webairig.truth/1", "sessionId": 2331328548, "frame": 150, "simTime": 12.1, "utcTime": "2026-11-20T23:01:02Z",
  "camera": {
    "id": 0, "name": "FWD_L", "width": 2048, "height": 2048, "hfovDeg": 15, "vfovDeg": 15,
    "fx": 7778.1, "fy": 7778.1, "cx": 1024, "cy": 1024,
    "gimbalAzDeg": 21.9, "gimbalElDeg": -0.6,
    "lat": 35.0, "lon": -117.0, "alt": 5703.2, "enu": [e, n, u],
    "rNedFromCam": [9 values, row-major],       // camera axes in local NED: x boresight, y right, z down
    "yawDeg": 0, "pitchDeg": -68, "rollDeg": 0,  // boresight Euler angles (degenerate at exact nadir; prefer the matrix)
    "boresightGround": [lat, lon],
    "footprint": [[lat, lon] x4]                 // TL, TR, BR, BL ground corners on the flat ground
  },
  "ownship": { "entityId": 0, "typeName": "B777", "lat": ..., "lon": ..., "alt": ..., "yawDeg": ..., "pitchDeg": ..., "rollDeg": ..., "velNed": [n, e, d] },
  "objects": [{
    "entityId": 6, "typeId": 20, "typeName": "MALE_UAV", "classId": 2, "className": "uav", "isOwnship": false,
    "bbox": [x1, y1, x2, y2],                    // tight box from projected model vertices, clipped to the image
    "bboxUnclipped": [x1, y1, x2, y2],
    "truncated": false, "visibleFraction": 1.0,  // fraction of sampled model points inside the image
    "centerPx": [u, v], "rangeM": 1614.2,
    "offBoresightXDeg": 0.1, "offBoresightYDeg": -0.2,
    "lat": ..., "lon": ..., "alt": ..., "yawDeg": ..., "pitchDeg": ..., "rollDeg": ..., "velNed": [n, e, d]
  }]
}
```

### What the truth fields mean

- **Which objects are listed.** Every entity with at least one sampled model point projecting inside the image.
  An entity entirely in front of the camera is also listed when its box overlaps the image, which happens on a
  close-up.
- **Ownship.** Listed with `isOwnship: true` whenever part of it is in view.
- **Occlusion.** Not modeled: `visibleFraction` counts points inside the image, not visible pixels.
- **Velocity.** `velNed` is computed by finite difference of successive host positions.
- **Dataset labels.** The consumer's recorder writes YOLO labels as `classId cx cy w h`, normalized, from
  `bbox`.

## 5. DIS

| Item | Value |
|---|---|
| PDU | IEEE 1278.1-2012 (protocol version 7) Entity State PDU, 144 bytes, no articulation records |
| Rate | `Dis:RateHz` for every entity |
| Entity ID | (`SiteId`, `ApplicationId`, CIGI entity ID + 1) |
| Entity type | From the catalog's `dis` block. The category values are defaults; check them against SISO-REF-010. |
| Location | ECEF, f64 |
| Orientation | ψ/θ/φ relative to ECEF |
| Velocity | ECEF, f32 |
| Dead reckoning | Algorithm 2 (FPW) |
| Marking | ASCII, up to 11 characters |
| Timestamp | Absolute, taken from the host UTC clock |

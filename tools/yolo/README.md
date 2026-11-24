# YOLO26 tooling

The consumer runs YOLO through ONNX Runtime (DirectML on the GPU). These scripts produce the ONNX file.

**License note:** these scripts are MIT like the rest of WebAirIG, but they import
[Ultralytics](https://github.com/ultralytics/ultralytics), which is **AGPL-3.0**. So are the YOLO26 weights and
models trained from them. The weights are not committed; see [models/yolo/README.md](../../models/yolo/README.md)
for the quick fetch-and-export steps.

## Setup

```powershell
python -m venv .venv-yolo
.\.venv-yolo\Scripts\Activate.ps1
pip install -r tools/yolo/requirements.txt
```

If PyTorch has no wheels for your Python version yet, create the venv with Python 3.12 or 3.13 instead
(`py -3.12 -m venv .venv-yolo`).

On Windows, PyPI installs the **CPU-only** torch. That is fine for exporting to ONNX. For GPU training, install the
CUDA build from pytorch.org.

If Ultralytics' automatic download of the weights fails, download them yourself and pass the local path:

```powershell
Invoke-WebRequest https://github.com/ultralytics/assets/releases/download/v8.4.0/yolo26n.pt -OutFile models/yolo/yolo26n.pt
python tools/yolo/export_onnx.py --model models/yolo/yolo26n.pt
```

## 1. Baseline: a stock COCO model

```powershell
python tools/yolo/export_onnx.py                 # writes models/yolo/yolo26n.onnx (the consumer's default path)
```

COCO models know "airplane", not MWIR jet-palette targets, so expect weak results. Keep the consumer's
**class-agnostic matching** switched on for this baseline.

## 2. Record an auto-labeled dataset

In the consumer, open **Dataset recorder** and click **Start**. Labels come from the truth sidecar, so no hand
labeling is needed. Class IDs are the `classId` values in `config/entity_catalog.json`. Point the cameras at
targets from the host UI (Track entity mode helps), vary FOV, gain and level, and let it run.

## 3. Train and export

```powershell
python tools/yolo/train.py --data output/datasets/webairig_YYYYMMDD_HHMMSS/dataset.yaml --imgsz 1024 --epochs 100
```

This writes `models/yolo/webairig.onnx`. Load it on the consumer's **Detector** page and switch
**class-agnostic matching** off to get per-class metrics.

## Notes

- **Heads.** Ultralytics 8.4.142 and later export the classic `[1, 4+nc, N]` head by default. These scripts pass
  `nms=False` to get the NMS-free `[1, 300, 6]` head instead. The consumer detects which head the model has and
  handles both.
- **Input size.** Frames are 2048×2048, and the consumer shrinks them to the model input with an area filter.
  Small or distant targets need `imgsz` 1024 or larger.
- **Augmentation.** In the jet palette, hue carries the temperature signal, so `train.py` turns hue and
  saturation augmentation off.

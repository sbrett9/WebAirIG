# YOLO models

The consumer loads its detector from this folder. Its default model is **`models/yolo/yolo26n.onnx`**
(`Detector:ModelPath` in `src/WebAirIG.Consumer/appsettings.json`).

**The YOLO26 weights are not in this repository.** Ultralytics releases them under **AGPL-3.0**, and WebAirIG's
code is MIT. Fetch and export them yourself as shown below. Without them, the consumer still runs: the Detector
page shows "model not found", and video, truth, metrics and the dataset recorder keep working.

## Get the stock YOLO26n model (one time)

From the repo root, in PowerShell 7:

```powershell
# 1. Python tools (Python 3.12+; see tools/yolo/README.md if PyTorch has no wheels for your version)
python -m venv .venv-yolo
.\.venv-yolo\Scripts\Activate.ps1
pip install -r tools/yolo/requirements.txt

# 2. Download the weights from the Ultralytics release on GitHub
Invoke-WebRequest https://github.com/ultralytics/assets/releases/download/v8.4.0/yolo26n.pt -OutFile models/yolo/yolo26n.pt

# 3. Export the ONNX model the consumer loads (1024x1024 input, NMS-free head)
python tools/yolo/export_onnx.py --model models/yolo/yolo26n.pt
```

This leaves `models/yolo/yolo26n.pt` and `models/yolo/yolo26n.onnx` in place. Both are git-ignored. Restart the
consumer, or click **Load** on its Detector page.

Alternatively, `python tools/yolo/export_onnx.py` with no `--model` lets Ultralytics download `yolo26n.pt`
itself. If that download fails, use the PowerShell download above.

## Your own models

`tools/yolo/train.py` trains on datasets from the consumer's recorder and writes `models/yolo/webairig.onnx`. To use
it, enter its path on the consumer's Detector page and click **Load**. Models trained from Ultralytics weights are
also subject to Ultralytics' license terms.

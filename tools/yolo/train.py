"""Fine-tune YOLO26 on a dataset recorded by the WebAirIG consumer, then export it to ONNX.

    python tools/yolo/train.py --data output/datasets/webairig_YYYYMMDD_HHMMSS/dataset.yaml

Pseudo-IR note: in the jet palette, hue encodes apparent temperature. Hue/saturation augmentation would
corrupt that signal, so it is disabled here; brightness jitter is kept small.
"""
import argparse
import shutil
from pathlib import Path

from ultralytics import YOLO

REPO = Path(__file__).resolve().parents[2]


def main() -> None:
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--data", required=True, help="dataset.yaml written by the consumer's recorder")
    p.add_argument("--model", default="yolo26n.pt", help="starting weights (yolo26n/s/m/l/x.pt)")
    p.add_argument("--imgsz", type=int, default=1024)
    p.add_argument("--epochs", type=int, default=100)
    p.add_argument("--batch", type=int, default=16)
    p.add_argument("--device", default="0")
    p.add_argument("--name", default="webairig")
    p.add_argument("--no-export", action="store_true")
    args = p.parse_args()

    model = YOLO(args.model)
    results = model.train(
        data=args.data,
        imgsz=args.imgsz,
        epochs=args.epochs,
        batch=args.batch,
        device=args.device,
        project=str(REPO / "output" / "yolo_runs"),
        name=args.name,
        hsv_h=0.0,      # keep jet hue = temperature
        hsv_s=0.0,
        hsv_v=0.1,
        fliplr=0.5,
        flipud=0.5,     # nadir imagery has no preferred "up"
        degrees=180.0,  # any heading
        mosaic=1.0,
        scale=0.3,
    )

    best = Path(results.save_dir) / "weights" / "best.pt"
    print(f"Best weights: {best}")
    if not args.no_export:
        exported = Path(YOLO(str(best)).export(format="onnx", imgsz=args.imgsz, nms=False, dynamic=False, simplify=True))
        target = REPO / "models" / "yolo" / f"{args.name}.onnx"
        target.parent.mkdir(parents=True, exist_ok=True)
        if exported.resolve() != target.resolve():
            shutil.copy2(exported, target)
        print(f"ONNX model for the consumer: {target}")


if __name__ == "__main__":
    main()

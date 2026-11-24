"""Export an Ultralytics YOLO (YOLO26) model to ONNX for the WebAirIG consumer.

Examples:
    python tools/yolo/export_onnx.py                              # yolo26n.pt (COCO) -> models/yolo/yolo26n.onnx
    python tools/yolo/export_onnx.py --model runs/.../best.pt --name webairig
    python tools/yolo/export_onnx.py --classic                    # classic [1,4+nc,N] head (consumer runs NMS)
"""
import argparse
import shutil
from pathlib import Path

from ultralytics import YOLO

REPO = Path(__file__).resolve().parents[2]


def main() -> None:
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--model", default="yolo26n.pt", help="weights (.pt); stock names are downloaded automatically")
    p.add_argument("--imgsz", type=int, default=1024,
                   help="network input size; frames are 2048x2048, so 1024+ keeps small MWIR targets")
    p.add_argument("--classic", action="store_true",
                   help="export the classic head instead of the NMS-free end-to-end head")
    p.add_argument("--name", default=None, help="output file stem (default: model stem)")
    p.add_argument("--out", default=str(REPO / "models" / "yolo"), help="output folder")
    args = p.parse_args()

    model = YOLO(args.model)
    kwargs = dict(format="onnx", imgsz=args.imgsz, dynamic=False, simplify=True)
    if not args.classic:
        kwargs["nms"] = False  # NMS-free end-to-end head: output0 = [1, 300, 6] (x1, y1, x2, y2, score, class)
    exported = Path(model.export(**kwargs))

    out_dir = Path(args.out)
    out_dir.mkdir(parents=True, exist_ok=True)
    target = out_dir / f"{args.name or exported.stem}.onnx"
    if exported.resolve() != target.resolve():
        shutil.copy2(exported, target)
    print(f"ONNX model: {target}")
    print("Point the consumer at it: Detector page > Model path, or appsettings.json Consumer:Detector:ModelPath")


if __name__ == "__main__":
    main()

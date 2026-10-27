#!/usr/bin/env python3
"""Offline pose analysis for prerecorded videos only. No live capture or control."""
import argparse
import csv
from pathlib import Path

def main():
    parser = argparse.ArgumentParser(description="Evaluate human pose landmarks in prerecorded video")
    parser.add_argument("--video", required=True, type=Path)
    parser.add_argument("--model", default="yolo11n-pose.pt")
    parser.add_argument("--output", type=Path)
    parser.add_argument("--confidence", type=float, default=0.25)
    parser.add_argument("--max-frames", type=int, default=450)
    args = parser.parse_args()
    if not args.video.is_file():
        parser.error("Video file not found")
    if not 0 <= args.confidence <= 1 or args.max_frames <= 0:
        parser.error("Invalid confidence or max-frames")
    from ultralytics import YOLO
    import cv2

    output = args.output or args.video.with_suffix(".pose.csv")
    output.parent.mkdir(parents=True, exist_ok=True)
    model = YOLO(args.model)
    capture = cv2.VideoCapture(str(args.video))
    if not capture.isOpened():
        raise RuntimeError("Unable to open prerecorded video")

    names = ["nose", "left_eye", "right_eye", "left_ear", "right_ear",
             "left_shoulder", "right_shoulder", "left_elbow", "right_elbow",
             "left_wrist", "right_wrist", "left_hip", "right_hip",
             "left_knee", "right_knee", "left_ankle", "right_ankle"]
    frames = 0
    points = 0
    with output.open("w", encoding="utf-8", newline="") as file:
        writer = csv.writer(file)
        writer.writerow(["frame", "person_id", "landmark", "x", "y", "confidence"])
        try:
            while frames < args.max_frames:
                ok, frame = capture.read()
                if not ok:
                    break
                result = model.predict(source=frame, conf=args.confidence, verbose=False, device="cpu")[0]
                if result.keypoints is not None:
                    keypoints = result.keypoints.data.cpu().numpy()
                    for person_index, person in enumerate(keypoints):
                        for index, (x, y, confidence) in enumerate(person):
                            if confidence <= 0:
                                continue
                            writer.writerow([frames, person_index, names[index],
                                             f"{float(x):.3f}", f"{float(y):.3f}",
                                             f"{float(confidence):.4f}"])
                            points += 1
                frames += 1
        finally:
            capture.release()
    print(f"Frames: {frames}; landmarks: {points}; CSV: {output}")
    print("person_id is an index within each frame, not a persistent identity.")

if __name__ == "__main__":
    main()

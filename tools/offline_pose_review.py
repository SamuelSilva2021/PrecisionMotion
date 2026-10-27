#!/usr/bin/env python3
"""Visual inspection of pose estimates in prerecorded videos only."""
import argparse
import csv
from collections import defaultdict
from pathlib import Path

import cv2

SKELETON = [(5, 6), (5, 7), (7, 9), (6, 8), (8, 10), (5, 11), (6, 12),
            (11, 12), (11, 13), (13, 15), (12, 14), (14, 16)]
LANDMARKS = ["nose", "left_eye", "right_eye", "left_ear", "right_ear",
             "left_shoulder", "right_shoulder", "left_elbow", "right_elbow",
             "left_wrist", "right_wrist", "left_hip", "right_hip",
             "left_knee", "right_knee", "left_ankle", "right_ankle"]


def load_points(path):
    frames = defaultdict(lambda: defaultdict(dict))
    with path.open(newline="", encoding="utf-8-sig") as handle:
        reader = csv.DictReader(handle)
        required = {"frame", "person_id", "landmark", "x", "y", "confidence"}
        if not required.issubset(reader.fieldnames or []):
            raise ValueError("Invalid pose CSV header")
        for row in reader:
            frame = int(row["frame"])
            person = int(row["person_id"])
            name = row["landmark"]
            if name not in LANDMARKS:
                continue
            frames[frame][person][name] = (float(row["x"]), float(row["y"]), float(row["confidence"]))
    return frames


def draw(frame, persons, threshold):
    visible_people = 0
    for person_id, landmarks in persons.items():
        points = {}
        for index, name in enumerate(LANDMARKS):
            if name not in landmarks:
                continue
            x, y, conf = landmarks[name]
            if conf < threshold:
                continue
            points[index] = (int(round(x)), int(round(y)))
            cv2.circle(frame, points[index], 4, (0, 220, 255), -1)
        if points:
            visible_people += 1
        for a, b in SKELETON:
            if a in points and b in points:
                cv2.line(frame, points[a], points[b], (255, 180, 0), 2)
        if points:
            anchor = points[min(points)]
            cv2.putText(frame, f"Frame-local person {person_id}", (anchor[0], max(20, anchor[1] - 12)),
                        cv2.FONT_HERSHEY_SIMPLEX, 0.5, (255, 255, 255), 1)
    return visible_people


def main():
    parser = argparse.ArgumentParser(description="Annotate a prerecorded video with pose CSV landmarks")
    parser.add_argument("--video", type=Path, required=True)
    parser.add_argument("--csv", type=Path, required=True)
    parser.add_argument("--output", type=Path)
    parser.add_argument("--confidence", type=float, default=0.5)
    parser.add_argument("--max-frames", type=int, default=450)
    args = parser.parse_args()
    if not args.video.is_file() or not args.csv.is_file():
        parser.error("Video or CSV file does not exist")
    if not 0 <= args.confidence <= 1 or args.max_frames <= 0:
        parser.error("Invalid confidence or max-frames")

    frames = load_points(args.csv)
    output = args.output or args.video.with_name(args.video.stem + ".pose-review.mp4")
    output.parent.mkdir(parents=True, exist_ok=True)
    capture = cv2.VideoCapture(str(args.video))
    if not capture.isOpened():
        raise RuntimeError("Could not open video")
    fps = capture.get(cv2.CAP_PROP_FPS) or 30
    width = int(capture.get(cv2.CAP_PROP_FRAME_WIDTH))
    height = int(capture.get(cv2.CAP_PROP_FRAME_HEIGHT))
    writer = cv2.VideoWriter(str(output), cv2.VideoWriter_fourcc(*"mp4v"), fps, (width, height))
    if not writer.isOpened():
        capture.release()
        raise RuntimeError("Could not open output video writer")

    processed = 0
    frames_with_pose = 0
    frames_without_pose = 0
    try:
        while processed < args.max_frames:
            ok, frame = capture.read()
            if not ok:
                break
            visible = draw(frame, frames.get(processed, {}), args.confidence)
            if visible:
                frames_with_pose += 1
            else:
                frames_without_pose += 1
            cv2.rectangle(frame, (0, 0), (600, 55), (0, 0, 0), -1)
            cv2.putText(frame, f"Frame {processed} | pose candidates: {visible} | threshold {args.confidence:.2f}",
                        (12, 32), cv2.FONT_HERSHEY_SIMPLEX, 0.7, (255, 255, 255), 2)
            writer.write(frame)
            processed += 1
    finally:
        capture.release()
        writer.release()

    summary = output.with_suffix(".summary.txt")
    summary.write_text(
        f"Video: {args.video}\nPose CSV: {args.csv}\nOutput: {output}\n"
        f"Processed frames: {processed}\nFrames with visible pose points: {frames_with_pose}\n"
        f"Frames without visible pose points: {frames_without_pose}\n"
        f"Confidence threshold: {args.confidence}\n"
        "A frame without pose points does not establish that no person was present.\n"
        "Person IDs are local to each frame and are not persistent identities.\n",
        encoding="utf-8")
    print(f"Annotated video: {output}\nSummary: {summary}")


if __name__ == "__main__":
    main()

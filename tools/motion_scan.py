"""Find animation events in a video by inter-frame difference, without exporting every frame.

Motivation: locating a specific moment (a unit landing, a card being dragged) by hand means
exporting hundreds of thumbnails and eyeballing them. Frame differencing ranks candidate
moments by how much the screen changed, so the interesting timestamps come first.

This reports only. It does not judge what the change means.
"""
import argparse
import os
import sys

import av
import numpy as np

# Downscale before diffing: the events we care about are compositional changes, and a
# 4K frame-by-frame diff costs far more than it finds.
WORK_WIDTH = 480


def analyze(video, start, end, sample_fps, threshold_pct, max_hits, crop):
    container = av.open(video)
    stream = container.streams.video[0]
    stream.thread_type = "AUTO"
    src_fps = float(stream.average_rate or 30)
    step = max(1, int(round(src_fps / sample_fps)))

    previous = None
    index = 0
    rows = []
    for frame in container.decode(stream):
        t = float(frame.pts * stream.time_base) if frame.pts is not None else index / src_fps
        if t < start:
            index += 1
            continue
        if t >= end:
            break
        if index % step == 0:
            img = frame.to_image().convert("L")
            if crop:
                img = img.crop(crop)
            w, h = img.size
            if w > WORK_WIDTH:
                img = img.resize((WORK_WIDTH, max(1, h * WORK_WIDTH // w)))
            arr = np.asarray(img, dtype=np.int16)
            if previous is not None:
                # Fraction of pixels that moved by more than a visible step.
                changed = float((np.abs(arr - previous) > 24).mean()) * 100.0
                rows.append((t, changed))
            previous = arr
        index += 1
    container.close()

    if not rows:
        print("NO_FRAMES", file=sys.stderr)
        return

    peak = max(r[1] for r in rows) or 1.0
    print(f"frames_sampled={len(rows)} peak_change={peak:.2f}%")
    print(f"{'time':>9} {'changed%':>9} {'rel':>6}")
    hits = 0
    last_t = -99.0
    for t, changed in sorted(rows, key=lambda r: -r[1]):
        if changed < threshold_pct:
            break
        # Collapse plateaus into one hit; a single event spans several samples.
        if t - last_t < 1.0 / sample_fps * 1.5:
            continue
        last_t = t
        hits += 1
        print(f"{t:9.2f} {changed:9.2f} {changed / peak * 100:5.0f}%")
        if hits >= max_hits:
            break
    print("SCAN_OK", file=sys.stderr)


def main():
    p = argparse.ArgumentParser()
    p.add_argument("--video", required=True)
    p.add_argument("--start", type=float, default=0.0)
    p.add_argument("--end", type=float, default=None)
    p.add_argument("--fps", type=float, default=4.0, help="analysis sampling rate")
    p.add_argument("--threshold", type=float, default=1.0, help="min changed pixels %%")
    p.add_argument("--max-hits", type=int, default=30)
    p.add_argument("--crop", type=int, nargs=4, default=None,
                   help="left top width height in source pixels")
    a = p.parse_args()
    end = a.end if a.end is not None else 1e9
    analyze(a.video, a.start, end, a.fps, a.threshold, a.max_hits, a.crop)


if __name__ == "__main__":
    main()
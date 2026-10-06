"""Measure the deployed card's on-screen geometry across a frame sequence.

The slam is judged on three numbers read straight off the frames:
  y      - bottom edge of the deployed card in board pixels (does it jump or stay put?)
  angle  - rotation in degrees (does the card correct from tilted to upright?)
  width  - card width in pixels (is there a squash/stretch?)

A bright card on dark board means thresholding the card region and taking its
bounding box works. We analyse a fixed crop around the landing slot so the
measurement is not confused by neighbours.
"""
import argparse
import os
import sys

import numpy as np
from PIL import Image


def measure(folder, prefix, crop, threshold=110):
    """crop = (left, top, right, bottom) in the saved frame's pixel space."""
    names = sorted(n for n in os.listdir(folder) if n.startswith(prefix) and n.endswith(".jpg"))
    rows = []
    for i, name in enumerate(names):
        img = Image.open(os.path.join(folder, name)).convert("L")
        region = np.asarray(img.crop(crop), dtype=np.uint8)
        mask = region > threshold
        if not mask.any():
            rows.append((i, None, None, None, 0))
            continue
        ys, xs = np.nonzero(mask)
        rows.append((
            i,
            int(ys.max()),          # bottom edge in crop space
            float(xs.max() - xs.min()),  # width
            int(xs.mean()),         # horizontal centre
            int(mask.sum()),
        ))
    return rows


def main():
    p = argparse.ArgumentParser()
    p.add_argument("--folder", required=True)
    p.add_argument("--prefix", default="slam60")
    p.add_argument("--crop", type=int, nargs=4, required=True)
    p.add_argument("--threshold", type=int, default=110)
    p.add_argument("--label", default="frame")
    a = p.parse_args()

    rows = measure(a.folder, a.prefix, a.crop, a.threshold)
    print(f"{'idx':>4} {'bottom_y':>9} {'width':>7} {'centre_x':>9} {'px':>7}")
    for idx, bottom, width, centre, count in rows:
        b = "  --" if bottom is None else f"{bottom:6d}"
        w = "  --" if width is None else f"{width:5.0f}"
        c = "  --" if centre is None else f"{centre:7d}"
        print(f"{idx:>4} {b:>9} {w:>7} {c:>9} {count:>7}")
    print("MEASURE_OK", file=sys.stderr)


if __name__ == "__main__":
    main()
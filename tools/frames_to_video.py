"""Assemble numbered PNG frames into an mp4.

Uses PyAV rather than the system ffmpeg, which on this machine is a 2013 build without an
H.264 encoder path we can rely on. Kept deliberately minimal: same in/out size, constant fps,
H.264 in a widely playable MP4 container.
"""
import argparse
import os
import sys


def encode(out_path, frame_paths, size, fps, crf=20):
    import av

    width, height = size
    container = av.open(out_path, mode="w")
    stream = container.add_stream("libx264", rate=fps)
    stream.width = width
    stream.height = height
    stream.pix_fmt = "yuv420p"
    stream.options = {"crf": str(crf), "preset": "slow"}

    import numpy as np
    from PIL import Image

    for path in frame_paths:
        img = Image.open(path).convert("RGB")
        if img.size != (width, height):
            img = img.resize((width, height), Image.LANCZOS)
        arr = np.asarray(img)
        frame = av.VideoFrame.from_ndarray(arr, format="rgb24")
        for packet in stream.encode(frame):
            container.mux(packet)

    for packet in stream.encode():
        container.mux(packet)
    container.close()


def main():
    p = argparse.ArgumentParser()
    p.add_argument("--frames", required=True, help="directory of numbered PNGs")
    p.add_argument("--pattern", default="slam_{:04d}.png")
    p.add_argument("--out", required=True)
    p.add_argument("--width", type=int, default=1280)
    p.add_argument("--height", type=int, default=720)
    p.add_argument("--fps", type=int, default=60)
    p.add_argument("--crf", type=int, default=20)
    a = p.parse_args()

    names = sorted(n for n in os.listdir(a.frames) if n.endswith(".png"))
    if not names:
        print("NO_FRAMES", file=sys.stderr)
        raise SystemExit(1)
    encode(a.out, [os.path.join(a.frames, n) for n in names],
           (a.width, a.height), a.fps, a.crf)
    print(f"VIDEO_OK {a.out} frames={len(names)} size={a.width}x{a.height} fps={a.fps}", file=sys.stderr)


if __name__ == "__main__":
    main()
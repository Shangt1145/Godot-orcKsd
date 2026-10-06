"""Extract frames from the KARDS reference match for frame-accurate slam study.

The bundled ffmpeg on this machine is a 2013 build that cannot decode HEVC (hvc1),
so decoding goes through PyAV instead. Frames are written as JPEG at 1:1 pixels for
the board region so the landing frame can be measured rather than eyeballed.
"""
import argparse
import os
import sys


def extract(video, out_dir, start, end, fps, scale, prefix, crop=None):
    import av
    from PIL import Image

    os.makedirs(out_dir, exist_ok=True)
    container = av.open(video)
    stream = container.streams.video[0]
    stream.thread_type = "AUTO"

    # fps=30 source; step keeps an integer number of source frames between samples.
    src_fps = float(stream.average_rate or 30)
    step = max(1, int(round(src_fps / fps)))

    written = 0
    index = 0
    for frame in container.decode(stream):
        t = float(frame.pts * stream.time_base) if frame.pts is not None else index / src_fps
        if t < start:
            index += 1
            continue
        if t >= end:
            break
        if index % step == 0:
            img = frame.to_image()
            if scale:
                img = img.resize(tuple(scale), Image.LANCZOS)
            if crop:
                img = img.crop(crop)
            name = f"{prefix}_{written:03d}.jpg"
            img.save(os.path.join(out_dir, name), quality=92)
            written += 1
        index += 1

    container.close()
    print(f"FRAMES_OK written={written} from {start:.2f}s to {end:.2f}s", file=sys.stderr)


def main():
    p = argparse.ArgumentParser()
    p.add_argument("--video", required=True)
    p.add_argument("--out", required=True)
    p.add_argument("--start", type=float, required=True)
    p.add_argument("--end", type=float, required=True)
    p.add_argument("--fps", type=float, default=10)
    p.add_argument("--scale", type=int, nargs=2, default=None)
    p.add_argument("--crop", type=int, nargs=4, default=None,
                   help="left top width height in source pixels")
    p.add_argument("--prefix", default="f")
    a = p.parse_args()
    extract(a.video, a.out, a.start, a.end, a.fps, a.scale, a.prefix, a.crop)


if __name__ == "__main__":
    main()
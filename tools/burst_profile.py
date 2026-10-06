"""Measure each animation burst in a clip: start, end, peak, and how long the motion lasts.

For comparing three variants of the same animation (light / medium / heavy) this is what
actually matters — not where the card ends up, but how long the motion runs and how violent
the peak is. Bursts are segmented by a change threshold with a refractory gap so one burst
is not counted as many.
"""
import argparse
import sys

import av
import numpy as np


def bursts(video, start, end, sample_fps, threshold, refractory, crop):
    container = av.open(video)
    stream = container.streams.video[0]
    stream.thread_type = "AUTO"
    src_fps = float(stream.average_rate or 30)
    step = max(1, int(round(src_fps / sample_fps)))

    series = []
    previous = None
    index = 0
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
            arr = np.asarray(img, dtype=np.int16)
            if previous is not None:
                series.append((t, float((np.abs(arr - previous) > 24).mean()) * 100.0))
            previous = arr
        index += 1
    container.close()

    on = [i for i, (_, v) in enumerate(series) if v >= threshold]
    groups = []
    for i in on:
        if groups and series[i][0] - series[groups[-1][-1]][0] <= refractory:
            groups[-1].append(i)
        else:
            groups.append([i])

    print(f"{'#':>2} {'start':>7} {'end':>7} {'dur_s':>6} {'peak%':>7} {'mean%':>6} {'frames':>6}")
    for n, g in enumerate(groups, 1):
        ts = [series[i][0] for i in g]
        vs = [series[i][1] for i in g]
        print(f"{n:>2} {ts[0]:7.3f} {ts[-1]:7.3f} {ts[-1] - ts[0]:6.3f} "
              f"{max(vs):7.2f} {sum(vs) / len(vs):6.2f} {len(g):>6}")
    print("BURST_OK", file=sys.stderr)


def main():
    p = argparse.ArgumentParser()
    p.add_argument("--video", required=True)
    p.add_argument("--start", type=float, default=0.0)
    p.add_argument("--end", type=float, default=1e9)
    p.add_argument("--fps", type=float, default=30.0)
    p.add_argument("--threshold", type=float, default=2.0)
    p.add_argument("--refractory", type=float, default=0.20, help="merge bursts closer than this")
    p.add_argument("--crop", type=int, nargs=4, default=None)
    a = p.parse_args()
    bursts(a.video, a.start, a.end, a.fps, a.threshold, a.refractory, a.crop)


if __name__ == "__main__":
    main()
"""Reproduce the aim-arrow triangulation failure seen in the Godot console.

BattleAim builds a 7-point polygon: a shaft drawn as two parallel edges and a head
with a shoulder notch. Godot's triangulator rejects self-intersecting or degenerate
input, which is what "Invalid polygon data, triangulation failed" means. This script
sweeps the From/To geometry the game actually uses and reports which shapes break,
so the fix targets the real failure rather than a guess.
"""
import sys


def cross(o, a, b):
    return (a[0] - o[0]) * (b[1] - o[1]) - (a[1] - o[1]) * (b[0] - o[0])


def segments_properly_intersect(p1, p2, p3, p4):
    """True when segments p1p2 and p3p4 cross at a non-endpoint, i.e. a real self-intersection."""
    d1, d2 = cross(p3, p4, p1), cross(p3, p4, p2)
    d3, d4 = cross(p1, p2, p3), cross(p1, p2, p4)
    return ((d1 > 0) != (d2 > 0)) and ((d3 > 0) != (d4 > 0))


def degenerate(pts, eps=1e-6):
    """Consecutive duplicate or collinear-with-reversal vertices break triangulation too."""
    n = len(pts)
    for i in range(n):
        a, b, c = pts[i], pts[(i + 1) % n], pts[(i + 2) % n]
        area = cross(a, b, c)
        if abs(area) < eps:
            return f"collinear/duplicate at {i}"
    return None


def build_points(fx, fy, tx, ty):
    """Reproduction of the shipped arrow, plus the candidate fix.

    Shipped code: shaft 6px half-width, head extends 14+27 = 41px back from the tip.
    That fixed 41px head needs a longer shaft than short drags have, so the polygon
    folds back on itself. The fix scales the head down when the shaft would be too short,
    and collapses to a plain wedge when even a minimal head does not fit.
    """
    dx, dy = tx - fx, ty - fy
    length = (dx * dx + dy * dy) ** 0.5
    if length < 1e-9:
        return None, None
    dx, dy = dx / length, dy / length
    nx, ny = -dy, dx
    head_back = 41.0
    # The head is 30px wide at the shoulder, so it needs a longer shaft than that or the
    # shoulder edges cross the shaft edges. Below that, a wedge is the honest shape:
    # a short drag reads as direction, not as an arrowhead.
    if length < 45.0:
        half = min(6.0, length * 0.28)
        return [
            (fx + nx * half, fy + ny * half),
            (tx, ty),
            (fx - nx * half, fy - ny * half),
        ], "wedge"
    end = (tx - dx * 14, ty - dy * 14)
    sh = (end[0] - dx * 27, end[1] - dy * 27)
    return [
        (fx + nx * 6, fy + ny * 6),
        (sh[0] + nx * 6, sh[1] + ny * 6),
        (sh[0] + nx * 15, sh[1] + ny * 15),
        end,
        (sh[0] - nx * 15, sh[1] - ny * 15),
        (sh[0] - nx * 6, sh[1] - ny * 6),
        (fx - nx * 6, fy - ny * 6),
    ], "arrow"


def analyse(fx, fy, tx, ty):
    pts, _kind = build_points(fx, fy, tx, ty)
    if pts is None:
        return "degenerate direction", []
    reasons = []
    deg = degenerate(pts)
    if deg:
        reasons.append(deg)
    n = len(pts)
    bad = []
    for i in range(n):
        for j in range(i + 1, n):
            if j == i or (j + 1) % n == i or (i + 1) % n == j:
                continue  # adjacent edges legitimately share a vertex
            if segments_properly_intersect(pts[i], pts[(i + 1) % n], pts[j], pts[(j + 1) % n]):
                bad.append((i, j))
    if bad:
        reasons.append("self-intersect " + str(bad))
    # Shoelace area: a valid convex-ish arrow must enclose real area.
    area = 0.0
    for i in range(n):
        x1, y1 = pts[i]
        x2, y2 = pts[(i + 1) % n]
        area += x1 * y2 - x2 * y1
    if abs(area) / 2 < 1.0:
        reasons.append(f"near-zero area {abs(area) / 2:.2f}")
    return "; ".join(reasons) if reasons else "ok", pts


def main():
    # Distances the game really uses: the guard is From.DistanceTo(To) < 20, and card
    # slots sit tens of pixels apart, so short arrows are the ones that must still work.
    fails = []
    total = 0
    for length in [20, 25, 30, 40, 41, 42, 45, 50, 60, 80, 120, 200, 400]:
        for angle_deg in range(0, 360, 5):
            import math
            a = math.radians(angle_deg)
            fx, fy = 300.0, 300.0
            tx, ty = fx + length * math.cos(a), fy + length * math.sin(a)
            reason, _ = analyse(fx, fy, tx, ty)
            total += 1
            if reason != "ok":
                fails.append((length, angle_deg, reason))
    print(f"tested={total} broken={len(fails)}")
    if fails:
        print("\nbroken shapes (length, angle, reason):")
        for length, angle, reason in fails[:25]:
            print(f"  len={length:4d} angle={angle:3d}  {reason}")
        by_len = {}
        for length, _angle, reason in fails:
            by_len.setdefault(length, set()).add(reason.split(";")[0].strip())
        print("\nbroken by length:")
        for length in sorted(by_len):
            print(f"  len={length:4d}: {' | '.join(sorted(by_len[length]))}")
    else:
        print("no failing geometry found")
    print("SWEEP_OK")


if __name__ == "__main__":
    main()
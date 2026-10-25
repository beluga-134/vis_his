#!/usr/bin/env python3
"""Drag test phase B: locate the slider thumb (bright blob) in the settings panel,
sample background strip + bubble pixels, emit drag params as JSON.
Panel layout (window-relative): panel right edge = w-10, width 344, top margin 44.
Slider row is the first row: thumb y around 60..110. Slider x span ~ [w-338, w-54].
"""
import json, sys
from PIL import Image

V = "/mnt/e/qq_codex/vis_his/snap_vision_wpf/scripts/verify"

def load(p):
    return Image.open(p).convert("RGB")

def find_thumb(img):
    w, h = img.size
    px = img.load()
    x0, x1 = max(0, w - 354), w - 10
    y0, y1 = 44, 140
    best = None
    for x in range(x0, x1):
        col = 0
        for y in range(y0, min(y1, h)):
            r, g, b = px[x, y]
            if r > 195 and g > 195 and b > 195:
                col += 1
        if col >= 7:  # thumb is ~11-22px tall, track only ~4px
            if best is None:
                best = [x, x]
            best[1] = x
    if best is None:
        return None
    cx = (best[0] + best[1]) // 2
    ys = [y for y in range(y0, min(y1, h))
          for xx in (cx,) if min(max(xx, 0), w-1) == xx
          if px[cx, y][0] > 195 and px[cx, y][1] > 195 and px[cx, y][2] > 195]
    cy = sum(ys) // len(ys) if ys else (y0 + y1) // 2
    return cx, cy

def strip_mean(img, x0, x1, y0, y1):
    px = img.load()
    w, h = img.size
    rs = gs = bs = n = 0
    for y in range(y0, min(y1, h)):
        for x in range(x0, min(x1, w)):
            r, g, b = px[x, y]
            rs += r; gs += g; bs += b; n += 1
    return (rs // n, gs // n, bs // n) if n else (0, 0, 0)

def find_bubbles(img, limit=3):
    """yellow-ish user bubble pixels (#FDCB6E family)"""
    px = img.load()
    w, h = img.size
    pts = []
    for y in range(60, h - 60, 6):
        for x in range(10, w - 10, 6):
            r, g, b = px[x, y]
            if r > 210 and 150 < g < 225 and b < 140 and r - b > 90:
                pts.append((x, y))
                if len(pts) >= limit:
                    return pts
    return pts

def main():
    rect = tuple(int(v) for v in open(f"{V}/drag_rect.txt").read().split())
    L, T, R, B = rect
    img = None
    shot = None
    for name in ("drag_0.png", "drag_0b.png"):
        try:
            cand = load(f"{V}/{name}")
        except Exception:
            continue
        th = find_thumb(cand)
        if th:
            img, shot, thumb = cand, name, th
            break
    if img is None:
        print("THUMB_NOT_FOUND")
        sys.exit(2)
    w, h = img.size
    tx, ty = thumb
    # drag direction: if thumb already right side, drag left to 0.15 else right to 0.85
    span_l, span_r = w - 338, w - 60
    frac = (tx - span_l) / max(1, span_r - span_l)
    if frac > 0.55:
        target = int(span_l + 0.15 * (span_r - span_l))
    else:
        target = int(span_l + 0.85 * (span_r - span_l))
    out = {
        "shot": shot,
        "L": L, "T": T, "w": w, "h": h,
        "thumb_abs_x": L + tx, "thumb_abs_y": T + ty,
        "target_abs_x": L + max(span_l + 10, min(span_r - 4, target)),
        "bg_before": strip_mean(img, 4, 9, 240, 340),
        "bubbles_before": [{"x": x, "y": y, "rgb": img.load()[x, y]} for x, y in find_bubbles(img)],
    }
    json.dump(out, open(f"{V}/drag_params.json", "w"))
    print(f"THUMB_OK shot={shot} thumb_rel=({tx},{ty}) frac={frac:.2f} target_rel={target} bubbles={len(out['bubbles_before'])}")

if __name__ == "__main__":
    main()

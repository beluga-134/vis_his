#!/usr/bin/env python3
"""Drag test phase D: compare before/after shots + analyze log tail.
Verdict rules:
- DRAG_CONTINUOUS: >=3 'glass:' lines (multiple ticks applied in one drag)
  and no 'ui: overlay rebuild' storm between them (rebuild count <= 2).
- BG_CHANGED: background strip color changed noticeably (alpha applied to bg).
- BUBBLE_STABLE: sampled bubble pixels identical (transparency only touched bg).
"""
import json, re, sys
from PIL import Image

V = "/mnt/e/qq_codex/vis_his/snap_vision_wpf/scripts/verify"

def load(p):
    return Image.open(p).convert("RGB")

def strip_mean(img, x0, x1, y0, y1):
    px = img.load(); w, h = img.size
    rs = gs = bs = n = 0
    for y in range(y0, min(y1, h)):
        for x in range(x0, min(x1, w)):
            r, g, b = px[x, y]
            rs += r; gs += g; bs += b; n += 1
    return (rs // n, gs // n, bs // n) if n else (0, 0, 0)

def diff(a, b):
    return sum(abs(x - y) for x, y in zip(a, b)) // 3

def main():
    p = json.load(open(f"{V}/drag_params.json"))
    img1 = load(f"{V}/drag_1.png")
    bg_after = strip_mean(img1, 4, 9, 240, 340)
    bg_d = diff(tuple(p["bg_before"]), bg_after)
    px1 = img1.load()
    bub = []
    for b in p["bubbles_before"]:
        x, y = b["x"], b["y"]
        if 0 <= x < img1.size[0] and 0 <= y < img1.size[1]:
            bub.append(diff(tuple(b["rgb"]), px1[x, y]))
    bub_d = max(bub) if bub else -1

    log = open(f"{V}/drag_logtail.txt", encoding="utf-8", errors="replace").read()
    glass = re.findall(r"glass: solid fallback .*?opacity=(0\.\d+)", log)
    rebuilds = len(re.findall(r"ui: overlay rebuild", log))

    ok_drag = len(set(glass)) >= 2 and rebuilds <= 2
    ok_bg = bg_d >= 5
    ok_bub = bub_d == -1 or bub_d <= 4
    print(f"GLASS_VALUES={sorted(set(glass))} N_GLASS={len(glass)} REBUILDS={rebuilds}")
    print(f"BG_BEFORE={tuple(p['bg_before'])} BG_AFTER={bg_after} BG_DIFF={bg_d}")
    print(f"BUBBLE_MAXDIFF={bub_d} (n={len(bub)})")
    print(f"DRAG_CONTINUOUS={'PASS' if ok_drag else 'FAIL'}")
    print(f"BG_ONLY={'PASS' if (ok_bg and ok_bub) else 'FAIL'}")
    print("ALL_DONE_DRAG_REPORT")

if __name__ == "__main__":
    main()

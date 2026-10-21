# RapidOCR 常驻服务：stdin/stdout 行 JSON 协议（DirectML GPU 加速，失败自动回退 CPU）
# 请求：{"image": "C:\\path\\to.png"}
# 响应：{"ok": true, "lines": [{"text","score","box"}], "layout": "行列对齐文本", "elapsed_ms": 950, "device": "dml"}
import json
import statistics
import sys
import time

sys.stdout.reconfigure(encoding="utf-8")
sys.stderr.reconfigure(encoding="utf-8")

from rapidocr_onnxruntime import RapidOCR


def build_engine():
    """优先 DirectML（RTX 4060 Ti 等 DX12 GPU），失败回退 CPU。"""
    try:
        engine = RapidOCR(det_use_dml=True, cls_use_dml=True, rec_use_dml=True)
        return engine, "dml"
    except Exception as exc:  # noqa: BLE001
        sys.stderr.write(f"DML unavailable ({exc}), fallback to CPU\n")
        return RapidOCR(), "cpu"


def layout_text(items):
    """按检测框坐标还原文档布局：y 聚成视觉行，行内按 x 排序，列间补空格。

    items: [{"x","y","x2","y2","cx","cy","text"}]
    """
    if not items:
        return ""
    heights = [it["y2"] - it["y"] for it in items]
    med_h = max(statistics.median(heights), 1.0)

    items.sort(key=lambda it: it["cy"])
    rows = []
    row_cys = []
    for it in items:
        placed = False
        if rows:
            # 与最后一行的中心 y 差小于 0.6 倍行高 → 同一视觉行（表格同一行单元格）
            if abs(it["cy"] - row_cys[-1]) < 0.6 * med_h:
                rows[-1].append(it)
                row_cys[-1] = statistics.median([x["cy"] for x in rows[-1]])
                placed = True
        if not placed:
            rows.append([it])
            row_cys.append(it["cy"])

    out_lines = []
    for row in rows:
        row.sort(key=lambda it: it["x"])
        parts = []
        for i, it in enumerate(row):
            if i > 0:
                prev = row[i - 1]
                gap = it["x"] - prev["x2"]
                # 列间距超过约 1.5 个字符宽 → 视为换列，补两空格
                char_w = max((prev["x2"] - prev["x"]) / max(len(prev["text"]), 1), 2.0)
                parts.append("  " if gap > 1.5 * char_w else "")
            parts.append(it["text"])
        out_lines.append("".join(parts))
    return "\n".join(out_lines)


engine, device = build_engine()

for line in sys.stdin:
    line = line.strip()
    if not line:
        continue
    try:
        req = json.loads(line)
        path = req["image"]
        t0 = time.perf_counter()
        result, _ = engine(path)
        elapsed_ms = int((time.perf_counter() - t0) * 1000)
        lines = []
        items = []
        if result:
            for box, text, score in result:
                xs = [float(p[0]) for p in box]
                ys = [float(p[1]) for p in box]
                lines.append({
                    "text": text,
                    "score": round(float(score), 4),
                    "box": [[round(x, 1), round(y, 1)] for x, y in box],
                })
                items.append({
                    "x": min(xs), "y": min(ys), "x2": max(xs), "y2": max(ys),
                    "cx": sum(xs) / 4, "cy": sum(ys) / 4, "text": text,
                })
        resp = {
            "ok": True,
            "lines": lines,
            "layout": layout_text(items),
            "elapsed_ms": elapsed_ms,
            "device": device,
        }
    except Exception as exc:  # noqa: BLE001
        resp = {"ok": False, "error": str(exc)}
    sys.stdout.write(json.dumps(resp, ensure_ascii=False) + "\n")
    sys.stdout.flush()

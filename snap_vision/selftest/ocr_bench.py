# RapidOCR 基准：冷启动（含模型加载）与热调用分别计时
import json
import sys
import time
from pathlib import Path

t_import0 = time.perf_counter()
from rapidocr_onnxruntime import RapidOCR
t_import = time.perf_counter() - t_import0

t_init0 = time.perf_counter()
engine = RapidOCR()
t_init = time.perf_counter() - t_init0

img = sys.argv[1] if len(sys.argv) > 1 else str(Path(__file__).parent / "bench-text.png")

# 热身一次（触发首个会话编译），再测 3 次取均值
result, _ = engine(img)

times = []
for _ in range(3):
    t0 = time.perf_counter()
    result, _ = engine(img)
    times.append(time.perf_counter() - t0)

lines = []
if result:
    for box, text, score in result:
        lines.append({"text": text, "score": round(float(score), 4)})

print(json.dumps({
    "import_s": round(t_import, 3),
    "init_s": round(t_init, 3),
    "warm_runs_s": [round(t, 3) for t in times],
    "warm_avg_s": round(sum(times) / len(times), 3),
    "n_lines": len(lines),
    "lines": lines,
}, ensure_ascii=False, indent=1))

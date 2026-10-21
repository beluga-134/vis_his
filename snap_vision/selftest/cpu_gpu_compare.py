import time
from rapidocr_onnxruntime import RapidOCR

IMG = r"E:\qq_codex\vis_his\snap_vision\selftest\bench-4k.png"

cpu = RapidOCR()
cpu(IMG)
t0 = time.perf_counter()
r, _ = cpu(IMG)
cpu_ms = int((time.perf_counter() - t0) * 1000)

dml = RapidOCR(det_use_dml=True, cls_use_dml=True, rec_use_dml=True)
dml(IMG)
t0 = time.perf_counter()
r2, _ = dml(IMG)
dml_ms = int((time.perf_counter() - t0) * 1000)

print(f"CPU_4K_ms: {cpu_ms}  lines: {len(r)}")
print(f"DML_4K_ms: {dml_ms}  lines: {len(r2)}")
print(f"speedup: {cpu_ms / max(dml_ms, 1):.2f}x")

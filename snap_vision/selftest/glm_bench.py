#!/usr/bin/env python3
"""glm-5.3-flash 视觉 OCR 基准：同图同任务，计时（连接+生成全程）"""
import base64
import json
import sys
import time
import urllib.request

MODELS = "/home/beluga/.pi/agent/models.json"
AUTH = "/home/beluga/.pi/agent/auth.json"
base = json.load(open(MODELS))["providers"]["zai-coding-cn"]["baseUrl"]
key = json.load(open(AUTH))["zai-coding-cn"]["key"]

img_path = sys.argv[1] if len(sys.argv) > 1 else "/mnt/e/qq_codex/vis_his/snap_vision/selftest/bench-text.png"
task = "逐字提取图中全部文字，按行输出，保持原始顺序。"

data_url = "data:image/png;base64," + base64.b64encode(open(img_path, "rb").read()).decode()

body = {
    "model": "glm-5.3-flash",
    "messages": [
        {"role": "system", "content": "你是视觉分析助手。提取文字（OCR）时逐字保留原文，保持原有结构和换行。默认用中文回答。"},
        {"role": "user", "content": [
            {"type": "image_url", "image_url": {"url": data_url}},
            {"type": "text", "text": task},
        ]},
    ],
    "max_tokens": 4096,
}

times = []
reply = ""
for _ in range(3):
    t0 = time.perf_counter()
    req = urllib.request.Request(
        base.rstrip("/") + "/chat/completions",
        data=json.dumps(body).encode(),
        headers={"Authorization": "Bearer " + key, "Content-Type": "application/json"},
    )
    with urllib.request.urlopen(req, timeout=180) as resp:
        v = json.loads(resp.read().decode())
    times.append(time.perf_counter() - t0)
    reply = v["choices"][0]["message"]["content"].strip()
    usage = v.get("usage", {})

print(json.dumps({
    "runs_s": [round(t, 2) for t in times],
    "avg_s": round(sum(times) / len(times), 2),
    "usage": usage,
    "reply_chars": len(reply),
    "reply_head": reply[:200],
}, ensure_ascii=False, indent=1))

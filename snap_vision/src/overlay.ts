import { invoke } from "@tauri-apps/api/core";
import { listen } from "@tauri-apps/api/event";
import "./overlay.css";

const stage = document.getElementById("stage")!;
const sel = document.getElementById("sel")!;
const sizeLabel = document.getElementById("size")!;

function hint(text: string): void {
  const el = document.getElementById("hint")!;
  el.textContent = text;
}

interface DragRect {
  x: number;
  y: number;
  w: number;
  h: number;
}

interface Rect {
  x: number;
  y: number;
  w: number;
  h: number;
}

function draw(r: Rect): void {
  sel.style.display = "block";
  sel.style.left = `${r.x}px`;
  sel.style.top = `${r.y}px`;
  sel.style.width = `${r.w}px`;
  sel.style.height = `${r.h}px`;
  sizeLabel.style.display = "block";
  sizeLabel.style.left = `${Math.max(r.x, 8)}px`;
  sizeLabel.style.top = `${Math.max(r.y - 26, 8)}px`;
  sizeLabel.textContent = `${Math.round(r.w)} × ${Math.round(r.h)}`;
}

function clearSelection(): void {
  sel.style.display = "none";
  sizeLabel.style.display = "none";
}

function normalize(a: { x: number; y: number }, b: { x: number; y: number }): Rect {
  return {
    x: Math.min(a.x, b.x),
    y: Math.min(a.y, b.y),
    w: Math.abs(a.x - b.x),
    h: Math.abs(a.y - b.y),
  };
}

// 主路径：钩子侧 Alt+右键拖选（物理像素）→ 转 CSS 坐标画框
let hookActive = false;
void listen<DragRect>("vision://drag", (event) => {
  hookActive = true;
  const dpr = window.devicePixelRatio || 1;
  const r = event.payload;
  draw({
    x: r.x / dpr,
    y: r.y / dpr,
    w: r.w / dpr,
    h: r.h / dpr,
  });
});

// 备用路径：左键拖框（页面自行算坐标，物理像素 = CSS × dpr）
let anchor: { x: number; y: number } | null = null;

stage.addEventListener("mousedown", (e) => {
  if (e.button !== 0 || hookActive) return;
  anchor = { x: e.clientX, y: e.clientY };
  draw(normalize(anchor, anchor));
});

window.addEventListener("mousemove", (e) => {
  if (!anchor) return;
  draw(normalize(anchor, { x: e.clientX, y: e.clientY }));
});

window.addEventListener("mouseup", async (e) => {
  if (e.button !== 0 || !anchor || hookActive) return;
  const r = normalize(anchor, { x: e.clientX, y: e.clientY });
  anchor = null;
  if (r.w < 8 || r.h < 8) {
    clearSelection();
    return;
  }
  const dpr = window.devicePixelRatio || 1;
  try {
    await invoke("finish_capture", {
      x: r.x * dpr,
      y: r.y * dpr,
      w: r.w * dpr,
      h: r.h * dpr,
    });
  } catch (err) {
    hint(`截图失败：${String(err)}`);
  }
});

window.addEventListener("keydown", (e) => {
  if (e.key === "Escape") {
    e.preventDefault();
    void invoke("cancel_capture");
  }
});

window.addEventListener("contextmenu", (e) => {
  e.preventDefault();
  void invoke("cancel_capture");
});

// 页面无背景图（透明窗口直接暗化实时桌面），双 rAF 确保首帧上屏后报活显示
requestAnimationFrame(() =>
  requestAnimationFrame(() => {
    void invoke("overlay_ready");
  }),
);

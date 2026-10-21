import { invoke } from "@tauri-apps/api/core";
import { listen } from "@tauri-apps/api/event";
import { getCurrentWindow } from "@tauri-apps/api/window";
import "./chat.css";

interface ChatMsg {
  role: "user" | "assistant";
  text: string;
  imagePath?: string;
}

type Engine = "auto" | "local" | "glm";

interface Settings {
  opacity: number; // 玻璃底色浓度 0.2–0.95
  engine: Engine;
  glassBg: string; // 窗口玻璃底色 sRGB hex
  userBubble: string; // sRGB hex，如 #4d84fe
  assistantBubble: string;
}

const DEFAULT_SETTINGS: Settings = {
  opacity: 0.55,
  engine: "auto",
  glassBg: "#181c24",
  userBubble: "#4d84fe",
  assistantBubble: "#e8eef8",
};

const ENGINE_LABELS: Record<Engine, string> = {
  auto: "自动（新截图走本地，追问转 GLM）",
  local: "仅本地 RapidOCR（快）",
  glm: "仅 GLM 视觉（深度）",
};

const DEFAULT_TASK =
  "识别这张截图：如果是表格，按行列结构逐字提取全部内容（保留原始行列对齐）；" +
  "如果是图表或曲线，读出关键数值、单位和趋势；如果是纯文字，逐字提取；" +
  "最后用中文一句话说明这张图是什么。不要臆测图中没有的信息。";

let messages: ChatMsg[] = [];
const dataUrlCache = new Map<string, string>();
let busy = false;
let turnGen = 0; // Esc 取消：使在途分析的回传作废
let settings: Settings = loadSettings();

function loadSettings(): Settings {
  try {
    const raw = localStorage.getItem("snap-vision-settings");
    if (raw) {
      const parsed = JSON.parse(raw) as Partial<Settings>;
      return {
        opacity: typeof parsed.opacity === "number" ? Math.min(0.95, Math.max(0.2, parsed.opacity)) : DEFAULT_SETTINGS.opacity,
        engine: parsed.engine === "local" || parsed.engine === "glm" ? parsed.engine : "auto",
        glassBg: isHex(parsed.glassBg) ? parsed.glassBg : DEFAULT_SETTINGS.glassBg,
        userBubble: isHex(parsed.userBubble) ? parsed.userBubble : DEFAULT_SETTINGS.userBubble,
        assistantBubble: isHex(parsed.assistantBubble) ? parsed.assistantBubble : DEFAULT_SETTINGS.assistantBubble,
      };
    }
  } catch {
    /* 忽略损坏的设置 */
  }
  return { ...DEFAULT_SETTINGS };
}

function isHex(value: unknown): value is string {
  return typeof value === "string" && /^#[0-9a-fA-F]{6}$/.test(value);
}

function hexToRgba(hex: string, alpha: number): string {
  const r = parseInt(hex.slice(1, 3), 16);
  const g = parseInt(hex.slice(3, 5), 16);
  const b = parseInt(hex.slice(5, 7), 16);
  return `rgba(${r}, ${g}, ${b}, ${alpha})`;
}

function saveSettings(): void {
  localStorage.setItem("snap-vision-settings", JSON.stringify(settings));
}

function applyOpacity(): void {
  document.documentElement.style.setProperty("--glass-alpha", String(settings.opacity));
}

function applyGlassBg(): void {
  const r = parseInt(settings.glassBg.slice(1, 3), 16);
  const g = parseInt(settings.glassBg.slice(3, 5), 16);
  const b = parseInt(settings.glassBg.slice(5, 7), 16);
  document.documentElement.style.setProperty("--glass-rgb", `${r}, ${g}, ${b}`);
}

function applyBubbleColors(): void {
  const root = document.documentElement.style;
  root.setProperty("--user-bubble-bg", hexToRgba(settings.userBubble, 0.34));
  root.setProperty("--user-bubble-border", hexToRgba(settings.userBubble, 0.5));
  root.setProperty("--assistant-bubble-bg", hexToRgba(settings.assistantBubble, 0.1));
  root.setProperty("--assistant-bubble-border", hexToRgba(settings.assistantBubble, 0.2));
}

const app = document.getElementById("app")!;
app.innerHTML = `
  <header class="titlebar" id="titlebar" data-tauri-drag-region>
    <div class="titlebar-name" data-tauri-drag-region>Snap Vision · 截图问答</div>
    <div class="titlebar-actions">
      <button id="btn-settings" title="设置">⚙</button>
      <button id="btn-hide" title="隐藏窗口（Alt+右键截图时会自动弹出）">—</button>
      <button id="btn-close" title="退出">✕</button>
    </div>
  </header>
  <div class="settings-panel" id="settings-panel" hidden>
    <div class="settings-row">
      <label for="set-opacity">透明度</label>
      <input type="range" id="set-opacity" min="0.2" max="0.95" step="0.05" />
      <span id="set-opacity-value"></span>
    </div>
    <div class="settings-row">
      <label for="set-engine">识别引擎</label>
      <select id="set-engine">
        <option value="auto"></option>
        <option value="local"></option>
        <option value="glm"></option>
      </select>
    </div>
    <div class="settings-row">
      <label for="set-glass">窗口底色</label>
      <input type="color" id="set-glass" />
      <label for="set-user-bubble" class="compact">我的气泡</label>
      <input type="color" id="set-user-bubble" />
    </div>
    <div class="settings-row">
      <label for="set-assistant-bubble">助手气泡</label>
      <input type="color" id="set-assistant-bubble" />
      <span class="settings-note">sRGB 十六进制</span>
      <button id="btn-reset-colors" class="link-like">恢复默认</button>
    </div>
    <div class="settings-tip">本地引擎：RapidOCR 常驻进程，约 1 秒；GLM：glm-5.3-flash，表格/图表更准。</div>
  </div>
  <main class="chat-log" id="log">
    <div class="sys-tip">按住 <b>Alt</b> 并点击 <b>右键</b> 开始截图，框选后自动交给识别引擎分析。</div>
  </main>
  <div class="status" id="status"><span class="dot idle"></span><span id="status-text">就绪</span></div>
  <footer class="composer">
    <textarea id="input" rows="2" placeholder="继续追问…（Enter 发送，Shift+Enter 换行）"></textarea>
    <button id="btn-send">发送</button>
  </footer>
`;

const log = document.getElementById("log")!;
const input = document.getElementById("input") as HTMLTextAreaElement;
const statusDot = document.querySelector<HTMLElement>("#status .dot")!;
const statusText = document.getElementById("status-text")!;
const settingsPanel = document.getElementById("settings-panel")!;

function setStatus(kind: "idle" | "busy" | "done" | "error", text: string): void {
  statusDot.className = `dot ${kind}`;
  statusText.textContent = text;
}

function nearBottom(): boolean {
  return log.scrollHeight - log.scrollTop - log.clientHeight < 80;
}

function scrollBottom(): void {
  log.scrollTop = log.scrollHeight;
}

async function ensureDataUrl(path: string): Promise<string> {
  let url = dataUrlCache.get(path);
  if (!url) {
    url = await invoke<string>("read_image_data_url", { path });
    dataUrlCache.set(path, url);
  }
  return url;
}

function openLightbox(url: string): void {
  const overlay = document.createElement("div");
  overlay.className = "lightbox";
  const img = document.createElement("img");
  img.src = url;
  overlay.appendChild(img);
  overlay.addEventListener("click", () => overlay.remove());
  document.getElementById("app")!.appendChild(overlay);
}

function renderMessage(msg: ChatMsg, forceScroll = false): void {
  const shouldFollow = forceScroll || nearBottom();
  const wrap = document.createElement("div");
  wrap.className = `bubble ${msg.role}`;
  if (msg.imagePath) {
    const img = document.createElement("img");
    img.className = "bubble-img";
    img.alt = msg.imagePath;
    img.title = "双击放大查看";
    img.addEventListener("dblclick", (e) => {
      e.preventDefault();
      void ensureDataUrl(msg.imagePath!).then(openLightbox).catch(() => undefined);
    });
    void ensureDataUrl(msg.imagePath)
      .then((url) => {
        img.src = url;
      })
      .catch(() => {
        img.replaceWith(document.createTextNode("[图片载入失败]"));
      })
      // 图片撑高后若仍跟底则补一次滚动；用户主动上翻时不打扰
      .finally(() => {
        if (shouldFollow) scrollBottom();
      });
    wrap.appendChild(img);
  }
  const text = document.createElement("div");
  text.className = "bubble-text";
  text.textContent = msg.text;
  wrap.appendChild(text);
  log.appendChild(wrap);
  if (shouldFollow) scrollBottom();
}

interface OcrLocalResult {
  text: string;
  lines: { text: string; score: number }[];
  elapsedMs: number;
  device: string;
}

async function runTurn(): Promise<void> {
  if (busy || messages.length === 0) return;
  const myGen = ++turnGen;
  busy = true;
  const last = messages[messages.length - 1];
  const hasAssistant = messages.some((m) => m.role === "assistant");
  const useLocal =
    last.imagePath !== undefined && (settings.engine === "local" || (settings.engine === "auto" && !hasAssistant));

  try {
    const t0 = performance.now();
    if (useLocal) {
      setStatus("busy", "本地 OCR 中…（Esc 取消）");
      const r = await invoke<OcrLocalResult>("ocr_local", { imagePath: last.imagePath });
      if (myGen !== turnGen) {
        setStatus("idle", "已取消");
        return;
      }
      const text = r.text || "（未识别到文字）";
      const dev = r.device === "dml" ? "GPU" : "CPU";
      // 全链路：从松开右键（Rust 钩子时间戳）到回复渲染；拿不到锚点时退回本轮起点
      const totalMs = lastCaptureTs ? Math.max(1, Math.round(Date.now() - lastCaptureTs)) : Math.round(performance.now() - t0);
      lastCaptureTs = null;
      const reply = `[本地 RapidOCR · ${dev} · 推理${r.elapsedMs}ms · 全链路${totalMs}ms]\n${text}`;
      messages.push({ role: "assistant", text: reply });
      renderMessage({ role: "assistant", text: reply });
      setStatus("done", `本地 OCR 完成（推理 ${r.elapsedMs}ms / 全链路 ${totalMs}ms）`);
    } else {
      setStatus("busy", "GLM 分析中…（Esc 取消）");
      const reply = await invoke<string>("vision_chat", {
        messages: messages.map((m) => ({
          role: m.role,
          text: m.text,
          imagePath: m.imagePath ?? null,
        })),
      });
      if (myGen !== turnGen) {
        setStatus("idle", "已取消");
        return;
      }
      messages.push({ role: "assistant", text: reply });
      renderMessage({ role: "assistant", text: reply });
      setStatus("done", "完成");
    }
  } catch (err) {
    if (myGen !== turnGen) {
      setStatus("idle", "已取消");
      return;
    }
    renderMessage({ role: "assistant", text: `⚠ ${String(err)}` });
    setStatus("error", "失败，可重试或重新截图");
  } finally {
    if (myGen === turnGen) busy = false;
  }
}

async function onNewCapture(path: string): Promise<void> {
  const msg: ChatMsg = { role: "user", text: DEFAULT_TASK, imagePath: path };
  messages.push(msg);
  renderMessage(msg, true);
  await runTurn();
}

interface CapturePayload {
  path: string;
  thumb: string;
  capturedAt?: number;
}

let lastCaptureTs: number | null = null;

void listen<CapturePayload>("vision://new-capture", (event) => {
  const payload = event.payload;
  if (payload?.thumb) {
    dataUrlCache.set(payload.path, payload.thumb);
  }
  lastCaptureTs = typeof payload?.capturedAt === "number" && payload.capturedAt > 0 ? payload.capturedAt : null;
  void onNewCapture(payload.path);
  input.focus();
}).catch((err) => {
  setStatus("error", `事件通道注册失败：${String(err)}`);
});

function sendText(): void {
  const text = input.value.trim();
  if (!text || busy) return;
  input.value = "";
  const msg: ChatMsg = { role: "user", text };
  messages.push(msg);
  renderMessage(msg, true);
  void runTurn();
}

document.getElementById("btn-send")!.addEventListener("click", sendText);

input.addEventListener("keydown", (e) => {
  // Esc：取消进行中的分析（在途回传作废）
  if (e.key === "Escape" && busy) {
    e.preventDefault();
    turnGen++;
    busy = false;
    setStatus("idle", "已取消本次分析");
    return;
  }
  // isComposing 保护中文输入法候选确认不误发
  if (e.key === "Enter" && !e.shiftKey && !e.isComposing) {
    e.preventDefault();
    sendText();
  }
});

// ---- 设置面板 ----
const btnSettings = document.getElementById("btn-settings")!;
const opacityInput = document.getElementById("set-opacity") as HTMLInputElement;
const opacityValue = document.getElementById("set-opacity-value")!;
const engineSelect = document.getElementById("set-engine") as HTMLSelectElement;

for (const [value, label] of Object.entries(ENGINE_LABELS)) {
  const option = engineSelect.querySelector<HTMLOptionElement>(`option[value="${value}"]`);
  if (option) option.textContent = label;
}
opacityInput.value = String(settings.opacity);
opacityValue.textContent = String(settings.opacity);
engineSelect.value = settings.engine;
const userBubbleInput = document.getElementById("set-user-bubble") as HTMLInputElement;
const assistantBubbleInput = document.getElementById("set-assistant-bubble") as HTMLInputElement;
const glassInput = document.getElementById("set-glass") as HTMLInputElement;
userBubbleInput.value = settings.userBubble;
assistantBubbleInput.value = settings.assistantBubble;
glassInput.value = settings.glassBg;
applyOpacity();
applyGlassBg();
applyBubbleColors();

btnSettings.addEventListener("click", (e) => {
  e.stopPropagation();
  settingsPanel.hidden = !settingsPanel.hidden;
});

document.addEventListener("click", (e) => {
  if (!settingsPanel.hidden && !settingsPanel.contains(e.target as Node) && e.target !== btnSettings) {
    settingsPanel.hidden = true;
  }
});

opacityInput.addEventListener("input", () => {
  settings.opacity = Number(opacityInput.value);
  opacityValue.textContent = opacityInput.value;
  applyOpacity();
  saveSettings();
});

engineSelect.addEventListener("change", () => {
  settings.engine = engineSelect.value as Engine;
  saveSettings();
  setStatus("idle", `引擎：${ENGINE_LABELS[settings.engine]}`);
});

function bindColor(
  el: HTMLInputElement,
  set: (v: string) => void,
): void {
  const apply = () => {
    set(el.value);
    applyGlassBg();
    applyBubbleColors();
    saveSettings();
  };
  el.addEventListener("input", apply);
  el.addEventListener("change", apply);
}

bindColor(glassInput, (v) => (settings.glassBg = v));
bindColor(userBubbleInput, (v) => (settings.userBubble = v));
bindColor(assistantBubbleInput, (v) => (settings.assistantBubble = v));

document.getElementById("btn-reset-colors")!.addEventListener("click", () => {
  settings.glassBg = DEFAULT_SETTINGS.glassBg;
  settings.userBubble = DEFAULT_SETTINGS.userBubble;
  settings.assistantBubble = DEFAULT_SETTINGS.assistantBubble;
  glassInput.value = settings.glassBg;
  userBubbleInput.value = settings.userBubble;
  assistantBubbleInput.value = settings.assistantBubble;
  applyGlassBg();
  applyBubbleColors();
  saveSettings();
});

// ---- 窗口控制 ----
document.getElementById("btn-hide")!.addEventListener("click", () => {
  getCurrentWindow().hide().catch((err) => setStatus("error", `隐藏失败：${String(err)}`));
});

document.getElementById("btn-close")!.addEventListener("click", () => {
  getCurrentWindow().close().catch((err) => setStatus("error", `关闭失败：${String(err)}`));
});

getCurrentWindow()
  .onFocusChanged(({ payload: focused }) => {
    if (focused) input.focus();
  })
  .catch(() => undefined);

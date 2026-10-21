# Snap Vision

按住 **Alt + 右键** → 框选屏幕区域 → 松开 → 截图自动进入毛玻璃对话窗，
交给 **glm-5.3-flash** 视觉模型分析（OCR / 图表读数 / 内容理解），可继续多轮追问。

## 结构

- `src-tauri/`：Rust。全局鼠标钩子（Alt+右键）、BitBlt 截屏、裁剪、
  OpenAI 兼容调用 glm-5.3-flash、overlay/聊天窗口管理
- `src/`：`chat.ts/.css` 毛玻璃对话窗；`overlay.ts/.css` 全屏框选层
- 前端构建：Vite 多入口（chat.html / overlay.html）

## 配置（零配置优先）

自动读取：
- `~/.pi/agent/models.json` → `providers."zai-coding-cn".baseUrl`
- `~/.pi/agent/auth.json` → `zai-coding-cn.key`

覆盖：exe 同目录放 `snap_vision.json`：`{"baseUrl": "...", "apiKey": "...", "model": "..."}`

## 检查 / 开发

Windows 原生（复用 qq_codex 便携工具链）：

```bat
scripts\check.cmd          :: cargo check
npm install
npx tauri dev              :: 需自备 node/tauri-cli，或借用 qq_codex .tooling
```

## 原型局限

- 仅主显示器；单图对话；聊天历史在内存（关窗即失）
- 毛玻璃依赖 Windows acrylic（Win10 1803+ / Win11；失败时退化为半透明）

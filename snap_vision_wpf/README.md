# Snap Vision（WPF 重构版）

**C# + WPF（.NET 8）** 重写 UI 与服务层；产品逻辑、数据格式、运行时布局与
Tauri/Flutter 版完全一致（运行文件互认）。起因：Flutter 3.47 Windows 端 Impeller
渲染器在"Markdown 气泡 + 第二条气泡"组合下内存风暴（1GB→3.5GB→进程死亡），
widget 层无解（RepaintBoundary 无效，二分已坐实）；WPF 自渲染 DirectX 管线，
无第三方合成器风险，空载 60-120MB。

## 结构

```
snap_vision_wpf/
├─ native/capture/            C++ 捕获模块（自 Flutter 版零改动复用）+ C 导出层
│   ├─ capture_module.cpp/h   全局钩子(Alt+右键/Esc)、BitBlt 冻屏、分层选框挖洞、BGRA→RGBA
│   ├─ sv_exports.cpp         sv_init_capture/sv_free_buffer（P/Invoke 入口）
│   └─ build.cmd              cl x64 → artifacts/native/capture.dll
├─ src/SnapVision/            C# 主工程（net8.0-windows / WPF / x64）
│   ├─ Models/                ChatModels（消息/引擎/设置，与 chat_models.dart 1:1）
│   ├─ Services/              业务逻辑（与 lib/services/*.dart 1:1 移植）
│   │   ├─ AppPaths           exe 同目录路径约定 + 启动清理
│   │   ├─ AppLog             his_vis.log（毫秒时间戳前缀，1MB 轮转）
│   │   ├─ Config             snap_vision.json → ~/.pi/agent 兜底（zai-coding-cn）
│   │   ├─ Storage            pi 兼容 JSONL 会话（his_vis-*.jsonl，续写/轮换/恢复）
│   │   ├─ UiStore            snap_vision.ui.json（设置+数据库挂载列表）
│   │   ├─ OcrService         RapidOCR 常驻子进程（行 JSON 协议，30s 超时）
│   │   ├─ VisionClient       glm-5.3-flash OpenAI 兼容客户端（180s 超时）
│   │   ├─ DbMcpService       MCP stdio 客户端 + 5 轮工具循环 + 强制汇总
│   │   ├─ ImageUtils         PNG 编码/缩略图/dataURL（4.5MiB 上限，后台线程）
│   │   └─ CaptureNative      capture.dll P/Invoke 桥
│   └─ Ui/                    窗口骨架/聊天流/Markdown 渲染/面板（md_render.dart 移植）
├─ scripts/                   build.cmd / deploy.ps1 / 验证脚本
└─ artifacts/                 capture.dll、发布产物
```

## 产品逻辑保持不变（红线）

- Alt+右键框选 → 截图进毛玻璃置顶聊天窗 → 引擎分析（auto/local RapidOCR/glm/db 问答）
- 运行时文件全部 exe 同目录：`his_vis-*.jsonl`、`his_vis-cap-*.png`（3 天清理）、
  `his_vis.log`、`snap_vision.json`、`snap_vision.ui.json`、`db_prompt.txt`
- sidecars 向上五层探测（`.venv-ocr`+`scripts/` 优先，`sidecars/*.exe` 兜底）
- 资源红线：≤20% 系统资源（正常 <500MB），单实例
- 窗口行为：无边框+置顶+截图完成立即前置（聊天窗全程可见）

## 构建与部署（Windows）

```bat
rem ① 捕获模块（一次性，改 C++ 才需重跑）
cd native\capture && build.cmd

rem ② 主程序
cd src\SnapVision
%LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe publish -c Release -r win-x64 --self-contained false -o ..\..\artifacts\publish

rem ③ 部署到运行时目录
powershell -File ..\..\scripts\deploy.ps1
```

部署目标：`..\snap_vision\bin\snap-vision.exe`（与旧版同名替换；
Flutter 遗留的 flutter_windows.dll/data/ 在 M5 清理）。

## 里程碑

- [x] M0 工程骨架 + 工具链（.NET 8 SDK 用户级、capture.dll 编译）
- [x] M1 服务层 1:1 移植（Dart→C#，格式零变化）
- [x] M2 原生桥 + 窗口骨架（acrylic/无边框/置顶/拖拽缩放/钩子）
- [x] M3 聊天 UI（气泡流/Markdown/输入框/面板/恢复）
- [x] M4 全量验证（钩子/OCR/GLM/DB 流程/内存采样 ≤500MB）
- [x] M5 删除 Flutter 版源码与 bin 遗留文件

## 决策记录

- 2026-10-24：放弃 Flutter（Impeller 内存风暴不可救，见 ../snap_vision_flutter/REBUILD_STATUS.md），
  选 C# + WPF：同品类成熟产品（PowerToys/ScreenToGif/Captura/ShareX）均为 C# 栈；
  WPF 自渲染无第三方合成器；NuGet 生态 + WinUI3/Avalonia 迁移线保证拓展性。
- Markdown 渲染：不引第三方库，1:1 移植 md_render.dart（567 行 Dart→C#），
  与产品现有样式/安全子集完全一致。

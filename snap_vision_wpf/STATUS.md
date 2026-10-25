# Snap Vision WPF 重构 · 状态与验证记录（2026-10-24）

## 结论：M0–M4 全部完成，WPF 版已取代 Flutter 版上线 bin/

| 里程碑 | 状态 | 证据 |
|---|---|---|
| M0 工具链 | ✅ | .NET 8 SDK 8.0.426（用户级）；capture.dll 208KB（cl x64） |
| M1 服务层移植 | ✅ | Dart→C# 1:1，编译 0 错误；格式与三代版本互认 |
| M2 原生桥+窗口 | ✅ | 钩子/冻屏/选框挖洞/裁剪全链路实测通过 |
| M3 聊天 UI | ✅ | 气泡/MD/面板/灯箱/输入区（视觉模型核验截图） |
| M4 全量验证 | ✅ | 冒烟 + 全量恢复 + E2E 拖选，内存稳 210–368MB |
| M5 删除 Flutter | ✅ | snap_vision_flutter/ 与 bin 遗留已清理 |

## 内存对比（同机同场景）

| 场景 | Flutter/Impeller | WPF |
|---|---|---|
| 单气泡 | 118MB 稳 | 210MB 稳（含单文件运行时） |
| 全量恢复 146 条 + MD | T+15s 1.4GB → 撑死 | **T+60s 260MB 平线** |
| E2E（截图+OCR+气泡） | — | 368MB |

用户红线（≤20% 系统资源 ≈6.4GB，正常 <500MB）满足。

## E2E 实测链路（模拟 Alt+右键拖选 400×200）

hook Alt+RBDown → BitBlt 冻屏 1920×1080 → 原生选框层（聊天窗挖洞）→ RBUp
→ 裁剪 → his_vis-cap-*.png 落盘 → config(glm-5.3-flash, key_len=49) → vision chat。
引擎 auto 走 GLM 属正确行为（恢复会话含 assistant 历史）。

## 过程中发现并修复的坑

1. **Dart 版 md_render.dart 标题分支缺 i++**（无限循环）——照抄移植时带入，
   C# 版已修并注释；Dart 版一直带雷但历史内容无 `#` 行未触发。
2. **WPF 元素不能挂双父**（两处）：
   a) MD 文档按文本缓存 → 同文本消息共用元素实例；
   b) 解析器 Emit 把元素同时挂进临时根面板 → 元素被标记逻辑父级，
   挂气泡面板即抛"指定的元素已经是另一个元素的逻辑子元素"。
   修复：去缓存 + 去临时面板（元素只收集不挂载）；修复后 md render failed 增量为 0。
3. WPF 无 PixelFormats.Rgba32 → BGRA32 + R/B 通道交换。
4. WindowChrome 标题区内的按钮需 IsHitTestVisibleInChrome=True。
5. SWCA Acrylic 在本机返回失败 → 自动实心底色兜底（glassBg @ 0xF5），
   后续可试 ACCENT_ENABLE_BLURBEHIND / Win11 DWMWA_SYSTEMBACKDTYPE。
6. cmd 批处理禁中文注释（GBK 代码页）；ps1 必须 UTF-8 BOM。
7. dotnet-install.ps1 不设 $LASTEXITCODE，判安装结果用 --list-sdks。

## 构建 / 部署 / 验证

```bat
cd snap_vision_wpf
native\capture\build.cmd          :: 一次性（改 C++ 才重跑）
scripts\build.cmd                 :: 发布 66MB 自包含单文件
powershell -File scripts\deploy.ps1          :: 部署到 snap_vision\bin
powershell -File scripts\deploy.ps1 -CleanFlutter  :: 同时清 bin 遗留
```

验证脚本：`scripts/verify/smoke.ps1`（冒烟+内存采样）、
`scripts/verify/e2e.ps1`（模拟拖选全链路）、
`scripts/verify/titlebar.ps1`（标题栏按钮/面板/拖拽/隐藏/清空全序列）、
`scripts/verify/dbmcp.ps1`（数据库 MCP 问答链路：自动切 engine=db→拖选表格→
观察 OCR→DeepSeek 查库→恢复 engine）、
`scripts/verify/probe_occlusion.ps1`（遮挡渲染节流探测）。

## 部署布局（2026-10-24 23:20 更新）

除正式部署目录外，vis_his 根另放一份运行副本（用户要求）：

```
E:/qq_codex/vis_his/
├─ snap-vision.exe          ← 运行副本（deploy.ps1 会自动同步更新）
├─ capture.dll
├─ sidecars/                ← 实体目录副本 537MB（2026-10-24 起，
│                              用户要求不用 junction；deploy.ps1 增量同步）
├─ his_vis-20261021-125619.jsonl ←→ 硬链接到 bin 同名文件（会话不分叉）
├─ snap_vision.json / snap_vision.ui.json / db_prompt.txt （副本）
└─ his_vis.log（根副本自己的日志）
```

注意：两个 exe 共用单实例互斥体，同时只能跑一个（新启动会杀旧接管）；
会话硬链接保证两边写同一文件。

## 标题栏按钮“无效”排查与修复（2026-10-24 晚）

用户报“上面的按钮没有生效”。排查出三个叠加的根因，全部修复：

1. **陈旧增量构建（工具链坑）**：16:42 那次“部署了 CaptionHeight=0 修复”实际
   发布的还是旧代码——WSL /mnt/e 下改文件后 msbuild 增量判定失效，未重新编译。
   对策：**从 WSL 改完源码必须先 `rm -rf src/SnapVision/obj src/SnapVision/bin
   artifacts/publish` 再构建**，且看构建日志里有没有真实编译行；deploy.ps1 不会校验
   产物新旧。
2. **面板零高度 bug（真正的“按钮无效”）**：RebuildOverlayPanels 把面板挂到主 Grid
   （默认 Row 0，仅 38px 高）且 Margin top=44 → 可用高度为负 → 面板被布局成
   0 高度永不显示。点击/状态切换其实都在工作，只是看不见。修复：挂到 XAML 里
   本就准备好的 OverlayHost（RowSpan=4 全高）。修复后 ⚙/🗄 面板均正常渲染
   （截图核验：设置面板含透明度/引擎/模型/色板；数据库面板含挂载列表+表统计）。
3. **遮挡渲染节流（观感“点了没反应”）**：本窗口 NOACTIVATE+TOPMOST，
   用户桌面常驻全屏应用（Retro Messenger），无输入时 WPF 渲染被节流——
   面板/高亮要等几秒才补帧。对策：1×1 透明元素无限动画保活 +
   状态变化后 ForceRender（ApplicationIdle 回调 + RedrawWindow UPDATENOW）。
   注意：WindowFromPoint 对 TOPMOST 窗口有误报，排查时别被它带偏。

实测通过（2026-10-24 17:19 全序列，埋点日志+像素比对）：⚙ 开面板 / 面板内点不关
（IsInOverlay 修复）/ 面板外点关 / 🗄 开库面板 / 🧹 清空（153→0）/ 拖拽移动
（DragMove dx=120 dy=80）/ — 隐藏 + Alt+右键自动弹出 / 本地 OCR（8 行 572ms dml）/
✕ 退出。另：合成点击测试时用户在机上操作会互相干扰（几次“窗口莫名隐藏”即用户本人点的），
验证脚本适合无人值守时跑。

保留的埋点：ui: click/settings/db/…、ui: overlay rebuild → …、ui: controller changed …
（his_vis.log，低频、便于诊断）。

## 晚间追加（2026-10-24 22:30–23:20）：启动接管 / 图标 / 滚动条 / 跨线程修复

用户反馈四项，全部完成：

1. **启动杀旧进程接管**：EnsureSingleInstance 改为“发现旧实例 → 整树 Kill
   （含 sidecar）→ 等互斥体 → 接管”，实测接管 0.7s。
   两个新踩坑：a) 命名互斥体即使 ALREADY_EXISTS 也返回有效句柄，
   重试循环里不 CloseHandle 的话名字被自己占死、永远重试失败；
   b) 旧实现“前置旧窗口”改为杀旧，枚举窗口的 P/Invoke 已删。
2. **exe 图标**：PIL 生成 app.ico（深底圆角 + 主题蓝截图框括号 + 中心视点，
   16–256 七尺寸）；csproj ApplicationIcon 嵌入 exe + Resource 供窗口/任务栏。
   已验证 ExtractAssociatedIcon 提取出本设计。
3. **滚动条美化**：App.xaml 隐式样式——透明轨道 + 10px 圆角滑块
   （#59A9B7CC，悬停 #994D84FE，拖拽 #CC4D84FE），竖/横两套模板，
   全应用统一（聊天区/输入框/面板列表）。
   踩坑：初版 22% 透明度滑块在深底上几乎不可见，像素扫描误判为“没渲染”，
   实际一直在（166 条消息时滑块仅 ~6px 高）——提亮到 35% 后确认。
4. **跨线程异常修复**（用户截图暴露）：DB 引擎流程的 onProgress 回调在
   ConfigureAwait(false) 后从线程池线程调 SetStatus → “调用线程无法访问
   此对象”变成 ⚠ 气泡。修复：ChatController.RaiseChanged() 统一
   Dispatcher.CheckAccess/BeginInvoke 编组。

另：ForceRender/诊断回调从 ApplicationIdle 改为 Background——保活动画
持续 tick 会让“空闲优先级”永不触发（Background 帧间可执行，不受影响）。

## 追加修复（2026-10-25 晚）：窗口拖不动（SC_DRAGMOVE）+ 启动慢排查

### 1. 窗口无法拖动 —— 已修

用户 23:31 拖动失败瞬间日志（决定性证据）：

```
ui: TitlebarDown src=TextBlock clickCount=1
+91ms  ui: RootMouseDown src=TextBlock at (0,0) btn=Released
```

MouseDown 里 btn=Released、坐标 (0,0)：事件被延迟处理时 WPF 缓存的左键状态
已不是 Pressed，DragMove() 抛 InvalidOperationException 被 catch 静默吞掉 →
拖不动。触发场景与此前滑块 Thumb 释放捕获的时序相关（当晚用户拖完透明度
滑块后点的标题栏）。修复：改走 Win32 SC_DRAGMOVE（0xF012）系统级拖动，
按物理输入状态判断，不依赖 WPF 事件时序；_hwnd 为空时回退 DragMove。
TitlebarDown 日志增加 btn= 字段便于后续诊断。

### 2. 启动慢 —— 仅排查未改（用户要求）

23:30 冷启动实测分段（182 条历史消息）：

```
main: start
+   34ms  controller ready（加载 ui.json + 会话 jsonl 读入内存，快）
+ 4477ms  glass: solid fallback   ← new MainWindow() 构造函数
+    2ms  capture bridge ready（OnSourceInitialized，随 Show 触发）
+  239ms  main: window shown
+  106ms  mcp: handshake ok（异步，不阻塞）
+   50ms  dbmcp: mounted
```

**主因：MainWindow 构造里的 RebuildChat() 在 UI 线程同步渲染全部 182 条
历史消息 + Markdown，~25ms/条 ≈ 4.5s。** 其余各段均 <0.3s；单文件自解压
发生在 Main 之前不在此日志内（66MB 压缩包，首次/缓存失效时另有 1–2s）。

可选优化方向（未实施）：①虚拟化/惰性渲染（只渲染可视区消息）；②先显窗
再后台恢复会话（占位“恢复中”）；③MD 渲染推迟到首次滚动。

## 追加修复（2026-10-25 凌晨三）：下拉框黑底白字（整套重写模板）

用户截图反馈：设置面板两个下拉框闭合框是 Aero2 默认浅灰底，配浅色
Foreground → 灰字灰底几乎不可读。10-24 曾试过仅设 Background 属性——无效，
默认模板根本不消费该属性。本次在 App.xaml 整套重写：

- 闭合框 = ToggleButton（#FF0E1218 黑底 + 右侧白▾箭头，悬停蓝色描边）
  + 选中项 ContentPresenter（白字、悬停 ToolTip 看全文仍由 ComboTrimTemplate 提供）
- 弹层 = #F510141C 深色圆角 Border + 内滚动列表；ComboBoxItem 重模板
  （透明底、悬停 #334D84FE、选中 #4D4D84FE、浅字）
- 隐式样式全局生效（设置面板/其他未来下拉共用）

另：拖拽修复已由日志验证——旧构建用户拖动 23 次 glass↔rebuild 1:1 风暴；
新构建模拟拖动只有 glass 行、零 rebuild（drag_logtail.txt 1–69 vs 73+ 行）。

## 追加修复（2026-10-25 凌晨二）：拖不动根因 + 连续拖动

用户实测反馈：“透明度按钮无法连续拖动”。日志实锤（02:35:32 起每 200–400ms
一次 `ui: overlay rebuild -> settings`）：SetOpacity 里的 Notify() →
OnControllerChanged → RebuildOverlayPanels，拖动中每个刻度都把面板（含正在
拖的滑块）销毁重建，拖拽必被打断。修复：

1. **SetOpacity 不再 Notify()**（保留 SaveUi + GlassChanged）：透明度只影响
   底色，不需要重建面板/气泡；数值文本由滑块自己的 ValueChanged 更新。
2. **连续拖动 + 0.1 步长**：去掉 IsSnapToTickEnabled（拖动中拇指会一格一格
   跳），改为 ValueChanged 实时 `Math.Round(v,1)` 应用，Thumb.DragCompleted
   松手后才吸附到 0.1 刻度。
3. **确认“只改背景”语义**：透明度仅写入 RootBorder.Background 的 alpha
   （亚克力 tint / 实底兜底两条路径同源），气泡/文字/面板均不受影响；
   验证脚本抽样像素比对（dragtest_a/c + find_thumb/drag_report）。

## 追加修复（2026-10-25 凌晨）：设置面板文字截断 + 透明度滑块

用户截图反馈两项，已修：

1. **识别引擎 ComboBox 文字被横向截断**（“数据库问答（截图→OCR→查库→”后面
   被硬切）：三重修复——a) 引擎标签缩短（Auto“自动（截图本地，追问 GLM）”、
   Db“数据库问答（OCR→查库→答）”，状态栏同步受益）；b) ComboBox 加
   ComboTrimTemplate（TextTrimming 省略号 + ToolTip 全文，闭合框/下拉列表共用，
   再长也只是“…”不会切半个字）；c) 面板 320→344 加宽。
2. **透明度滑块塌缩 + 范围改 0–1**：Slider 在横向 StackPanel 里无显式宽度，
   塌成只剩滑块小方块（截图里“没有滑轨”的具象）；改 DockPanel 填充式布局 +
   MinWidth=140。范围 0.2–0.95/步长0.05 → **0–1/步长0.1**（滑杆/钳制/加载钳制/
   默认值 0.55→0.6 四处同步；实底兑底 alpha 改线性 opacity×255，0=全透明底）。

涉及：ChatModels.cs、MainWindow.Panels.cs、ChatController.cs、UiStore.cs、
MainWindow.xaml.cs。

## 遗留小项（不阻塞使用）

- Acrylic 真毛玻璃未生效（实底兜底，观感一致度 ~95%）
- 恢复会话的旧截图缩略图不回显（与 Flutter 版行为一致，仅新截图有缩略图）
- 数据库 MCP 问答链路（engine=db → OCR → DeepSeek 查库）在 WPF 版尚未实测，
  待跑 `scripts/verify/dbmcp.ps1`（2026-10-24 晚 待验）
- 遮挡渲染节流的双保险（动画保活 + ForceRender）效果待用户实际体感确认

## 最终验证记录（2026-10-24 15:40）

- 修复后全量恢复：WS=387MB，md render failed 增量 = 0
- E2E：WS=357MB，钩子→冻屏→选框→裁剪→PNG→GLM 全链路通
- M5：bin 遗留 + snap_vision_flutter/ + mem_probe_* 已删除

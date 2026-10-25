// Snap Vision 原生捕获模块（hook.rs + capture.rs + selection.rs 的 C++ 移植）。
//
// 结构：
// - 钩子线程：WH_MOUSE_LL / WH_KEYBOARD_LL 常驻；Alt+右键进入拖选、
//   Esc 取消；所有命令 PostThreadMessage 给捕获线程，钩子回调立即返回。
// - 捕获线程：拥有冻结帧与选框窗（暗化层挖洞：聊天窗 + 当前选区），
//   BitBlt 冻屏、裁剪 BGRA→RGBA，结果回调（仍在本线程）交上层投递。
// - 上层（flutter_window.cpp）把回调 Post 到平台线程，经 MethodChannel
//   发给 Dart 存盘编码。
//
// 前台策略：截图完成/取消后立即把聊天窗 Show + SetForegroundWindow +
// 重新断言 HWND_TOPMOST——聊天窗全程可见（暗化层挖洞）且始终在前台。
#pragma once

#include <windows.h>

#include <cstdint>
#include <functional>
#include <vector>

namespace snapvision {

// 裁剪结果（RGBA，物理像素）。回调在捕获线程执行，内部数据会被搬走，
// 接收方负责复制/转移所有权。
struct CapturePayload {
  std::vector<uint8_t> rgba;
  int width;
  int height;
  int64_t capturedAtMs;
};

using CaptureCallback = std::function<void(CapturePayload payload)>;

// 初始化钩子与捕获线程。|chatHwnd|：Flutter 聊天窗（挖洞 + 完成后前置）。
void InitCaptureModule(HWND chatHwnd, CaptureCallback callback);

}  // namespace snapvision

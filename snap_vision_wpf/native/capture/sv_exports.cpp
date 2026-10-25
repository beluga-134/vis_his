// capture.dll 的 C 导出层：WPF/C# 经 P/Invoke 调用。
// 原模块（capture_module.cpp）零改动复用；本文件只做所有权转移：
// 捕获线程回调里的 std::vector 被 malloc 拷贝一份交给 C#，
// C# 侧 Marshal.Copy 完成后调用 sv_free_buffer 释放。

#include "capture_module.h"

#include <cstdlib>
#include <cstring>

extern "C" {

typedef void(__stdcall* SvCaptureCb)(unsigned char* rgba, int width, int height,
                                     long long captured_at_ms, void* ctx);

// 返回 false 表示 callback 为空。成功后钩子与捕获线程常驻。
__declspec(dllexport) BOOL __stdcall sv_init_capture(HWND chat_hwnd,
                                                     SvCaptureCb cb,
                                                     void* ctx) {
  if (cb == nullptr) return FALSE;
  snapvision::InitCaptureModule(chat_hwnd,
                                [cb, ctx](snapvision::CapturePayload payload) {
                                  const size_t n = payload.rgba.size();
                                  unsigned char* copy =
                                      (unsigned char*)malloc(n ? n : 1);
                                  if (copy == nullptr) return;
                                  if (n) memcpy(copy, payload.rgba.data(), n);
                                  cb(copy, payload.width, payload.height,
                                     payload.capturedAtMs, ctx);
                                });
  return TRUE;
}

__declspec(dllexport) void __stdcall sv_free_buffer(void* p) { free(p); }

}  // extern "C"

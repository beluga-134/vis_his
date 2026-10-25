// Snap Vision 原生捕获模块实现。见 capture_module.h 顶部说明。
#include "capture_module.h"

#include <atomic>
#include <chrono>
#include <cstdio>
#include <cwchar>
#include <cstring>
#include <string>

namespace snapvision {
namespace {

// ---------- 线程间命令（PostThreadMessage） ----------
constexpr UINT kMsgBegin = WM_APP + 1;
constexpr UINT kMsgMove = WM_APP + 2;
constexpr UINT kMsgEnd = WM_APP + 3;
constexpr UINT kMsgCancel = WM_APP + 4;

// ---------- 选框层视觉常量（与 selection.rs 一致） ----------
constexpr BYTE kDimAlpha = 56;        // 22% 黑纱
constexpr int kBorderPx = 2;          // 选框边宽（物理像素）
constexpr COLORREF kFrameColor = 0x00FFA34D;  // #4da3ff (0x00BBGGRR)
constexpr COLORREF kLabelBg = 0x00241A14;     // #141A24
constexpr COLORREF kLabelFg = 0x00FFE6CF;     // #cfe6ff
constexpr COLORREF kHintBg = 0x00201610;      // #101620
constexpr COLORREF kHintFg = 0x00FFF2EA;      // #eaf2ff
constexpr BYTE kLabelAlpha = 217;             // ≈0.85
constexpr BYTE kHintAlpha = 224;              // ≈0.88
constexpr int kFontHint = 20;
const wchar_t kHintText[] = L"松开右键完成截图，Esc 取消";

// ---------- 模块状态 ----------
std::atomic<bool> g_capturing{false};
std::atomic<bool> g_leftDrag{false};
std::atomic<int> g_dragStartX{0};
std::atomic<int> g_dragStartY{0};
std::atomic<uint64_t> g_lastMoveMs{0};
DWORD g_captureThreadId = 0;
HANDLE g_captureReady = nullptr;
HWND g_chatHwnd = nullptr;
CaptureCallback g_callback;
HHOOK g_mouseHook = nullptr;
HHOOK g_kbHook = nullptr;

// 捕获线程私有状态（无需加锁：仅捕获线程读写）
std::vector<uint8_t> g_frameBuf;  // BGRA top-down
int g_frameW = 0, g_frameH = 0;
bool g_frameValid = false;
HWND g_dim = nullptr, g_frame = nullptr, g_label = nullptr, g_hint = nullptr;
RECT g_screenRect{};
bool g_hasChatHole = false;
RECT g_chatHole{};
bool g_hasSel = false;
RECT g_selRect{};
std::wstring g_labelText;
HFONT g_font = nullptr;
HBRUSH g_frameBrush = nullptr, g_labelBrush = nullptr, g_hintBrush = nullptr;

// ---------- 日志：exe 同目录 his_vis.log ----------
void NativeLog(const std::string& msg) {
  wchar_t exePath[MAX_PATH];
  DWORD n = GetModuleFileNameW(nullptr, exePath, MAX_PATH);
  if (n == 0 || n >= MAX_PATH) return;
  std::wstring dir(exePath);
  size_t slash = dir.find_last_of(L"\\/");
  if (slash == std::wstring::npos) return;
  dir.resize(slash);
  std::wstring path = dir + L"\\his_vis.log";
  FILE* f = nullptr;
  if (_wfopen_s(&f, path.c_str(), L"ab") != 0 || !f) return;
  auto now = std::chrono::system_clock::now().time_since_epoch();
  long long ts =
      std::chrono::duration_cast<std::chrono::milliseconds>(now).count();
  fprintf(f, "%lld %s\n", ts, msg.c_str());
  fclose(f);
}

uint64_t NowMs() {
  return std::chrono::duration_cast<std::chrono::milliseconds>(
             std::chrono::system_clock::now().time_since_epoch())
      .count();
}

// ---------- 屏幕捕获（capture.rs 等价） ----------
bool CaptureScreen() {
  g_frameValid = false;
  HDC screenDc = GetDC(nullptr);
  if (!screenDc) return false;
  int width = GetSystemMetrics(SM_CXSCREEN);
  int height = GetSystemMetrics(SM_CYSCREEN);
  if (width <= 0 || height <= 0) {
    ReleaseDC(nullptr, screenDc);
    return false;
  }
  HDC memDc = CreateCompatibleDC(screenDc);
  HBITMAP bitmap = CreateCompatibleBitmap(screenDc, width, height);
  HGDIOBJ oldObj = SelectObject(memDc, bitmap);
  BOOL ok = BitBlt(memDc, 0, 0, width, height, screenDc, 0, 0, SRCCOPY);
  std::vector<uint8_t> buf((size_t)width * height * 4);
  if (ok) {
    BITMAPINFO info{};
    info.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
    info.bmiHeader.biWidth = width;
    info.bmiHeader.biHeight = -height;  // top-down
    info.bmiHeader.biPlanes = 1;
    info.bmiHeader.biBitCount = 32;
    info.bmiHeader.biCompression = BI_RGB;
    int lines = GetDIBits(memDc, bitmap, 0, height, buf.data(), &info,
                          DIB_RGB_COLORS);
    ok = lines == height;
  }
  SelectObject(memDc, oldObj);
  DeleteObject(bitmap);
  DeleteDC(memDc);
  ReleaseDC(nullptr, screenDc);
  if (!ok) return false;
  g_frameBuf = std::move(buf);
  g_frameW = width;
  g_frameH = height;
  g_frameValid = true;
  return true;
}

// 裁剪 + BGRA→RGBA（capture.rs::bgra_to_rgba_image 等价）。
bool CropRgba(int x, int y, int w, int h, CapturePayload* out) {
  if (!g_frameValid) return false;
  int x0 = x > 0 ? x : 0;
  int y0 = y > 0 ? y : 0;
  int x1 = (x + w) < g_frameW ? (x + w) : g_frameW;
  int y1 = (y + h) < g_frameH ? (y + h) : g_frameH;
  int cw = x1 - x0, ch = y1 - y0;
  if (cw < 1 || ch < 1) return false;
  std::vector<uint8_t> rgba((size_t)cw * ch * 4);
  for (int row = 0; row < ch; row++) {
    const uint8_t* src =
        g_frameBuf.data() + (((size_t)(y0 + row)) * g_frameW + x0) * 4;
    uint8_t* dst = rgba.data() + (size_t)row * cw * 4;
    for (int col = 0; col < cw; col++) {
      dst[col * 4 + 0] = src[col * 4 + 2];
      dst[col * 4 + 1] = src[col * 4 + 1];
      dst[col * 4 + 2] = src[col * 4 + 0];
      dst[col * 4 + 3] = src[col * 4 + 3];
    }
  }
  out->rgba = std::move(rgba);
  out->width = cw;
  out->height = ch;
  return true;
}

// ---------- 几何（selection.rs 可单测部分） ----------
bool Intersect(const RECT& a, const RECT& b, RECT* out) {
  LONG l = a.left > b.left ? a.left : b.left;
  LONG t = a.top > b.top ? a.top : b.top;
  LONG r = a.right < b.right ? a.right : b.right;
  LONG bt = a.bottom < b.bottom ? a.bottom : b.bottom;
  if (r > l && bt > t) {
    out->left = l;
    out->top = t;
    out->right = r;
    out->bottom = bt;
    return true;
  }
  return false;
}

POINT LabelOrigin(const RECT& sel, int labelH, const RECT& screen) {
  POINT p;
  p.x = sel.left > screen.left + 8 ? sel.left : screen.left + 8;
  LONG y = sel.top - labelH - 8;
  p.y = y > screen.top + 8 ? y : screen.top + 8;
  return p;
}

RECT FrameOuter(const RECT& sel) {
  return RECT{sel.left - kBorderPx, sel.top - kBorderPx,
              sel.right + kBorderPx, sel.bottom + kBorderPx};
}

// ---------- 选框窗 ----------
LRESULT CALLBACK FillProc(HWND hwnd, UINT msg, WPARAM wp, LPARAM lp) {
  if (msg == WM_ERASEBKGND) return 1;
  if (msg == WM_PAINT) {
    PAINTSTRUCT ps;
    HDC hdc = BeginPaint(hwnd, &ps);
    if (hdc) {
      RECT rc;
      GetClientRect(hwnd, &rc);
      HBRUSH brush = (hwnd == g_dim) ? (HBRUSH)GetStockObject(BLACK_BRUSH)
                                     : g_frameBrush;
      if (brush) FillRect(hdc, &rc, brush);
      EndPaint(hwnd, &ps);
    }
    return 0;
  }
  return DefWindowProc(hwnd, msg, wp, lp);
}

LRESULT CALLBACK LayerProc(HWND hwnd, UINT msg, WPARAM wp, LPARAM lp) {
  if (msg == WM_PAINT) {
    bool isLabel = hwnd == g_label;
    const std::wstring& text = isLabel ? g_labelText
                                       : std::wstring(kHintText);
    HBRUSH brush = isLabel ? g_labelBrush : g_hintBrush;
    COLORREF fg = isLabel ? kLabelFg : kHintFg;
    PAINTSTRUCT ps;
    HDC hdc = BeginPaint(hwnd, &ps);
    if (hdc) {
      RECT rc;
      GetClientRect(hwnd, &rc);
      FillRect(hdc, &rc, brush);
      HGDIOBJ old = SelectObject(hdc, g_font);
      SetBkMode(hdc, TRANSPARENT);
      SetTextColor(hdc, fg);
      RECT textRc = rc;
      DrawTextW(hdc, text.c_str(), -1, &textRc,
                DT_CENTER | DT_VCENTER | DT_SINGLELINE);
      SelectObject(hdc, old);
      EndPaint(hwnd, &ps);
    }
    return 0;
  }
  return DefWindowProc(hwnd, msg, wp, lp);
}

void RegisterOverlayClasses() {
  static bool registered = false;
  if (registered) return;
  registered = true;
  HINSTANCE inst = GetModuleHandle(nullptr);
  HCURSOR cursor = LoadCursor(nullptr, IDC_CROSS);
  g_frameBrush = CreateSolidBrush(kFrameColor);
  g_labelBrush = CreateSolidBrush(kLabelBg);
  g_hintBrush = CreateSolidBrush(kHintBg);
  g_font = CreateFontW(-kFontHint, 0, 0, 0, FW_NORMAL, 0, 0, 0,
                       DEFAULT_CHARSET, OUT_DEFAULT_PRECIS,
                       CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY, DEFAULT_PITCH,
                       L"Microsoft YaHei");

  WNDCLASSW wcDim{};
  wcDim.style = CS_HREDRAW | CS_VREDRAW;
  wcDim.lpfnWndProc = FillProc;
  wcDim.hInstance = inst;
  wcDim.hCursor = cursor;
  wcDim.hbrBackground = (HBRUSH)GetStockObject(BLACK_BRUSH);
  wcDim.lpszClassName = L"SnapVisionSelDim";
  RegisterClassW(&wcDim);

  WNDCLASSW wcLayer{};
  wcLayer.style = CS_HREDRAW | CS_VREDRAW;
  wcLayer.lpfnWndProc = LayerProc;
  wcLayer.hInstance = inst;
  wcLayer.hCursor = cursor;
  wcLayer.hbrBackground = nullptr;
  wcLayer.lpszClassName = L"SnapVisionSelLayer";
  RegisterClassW(&wcLayer);

  WNDCLASSW wcFrame{};
  wcFrame.style = CS_HREDRAW | CS_VREDRAW;
  wcFrame.lpfnWndProc = FillProc;
  wcFrame.hInstance = inst;
  wcFrame.hCursor = cursor;
  wcFrame.hbrBackground = g_frameBrush;
  wcFrame.lpszClassName = L"SnapVisionSelFrame";
  RegisterClassW(&wcFrame);
}

HWND CreateLayer(const wchar_t* className, DWORD ex, int x, int y, int w,
                 int h) {
  return CreateWindowExW(ex, className, nullptr, WS_POPUP, x, y, w, h, nullptr,
                         nullptr, GetModuleHandle(nullptr), nullptr);
}

POINT MeasureText(HWND hwnd, const std::wstring& text) {
  HDC dc = GetDC(hwnd);
  POINT size{0, 0};
  if (!dc) return size;
  HGDIOBJ old = SelectObject(dc, g_font);
  RECT rc{0, 0, 1000, kFontHint * 3};
  DrawTextW(dc, text.c_str(), -1, &rc, DT_CALCRECT | DT_SINGLELINE);
  SelectObject(dc, old);
  ReleaseDC(hwnd, dc);
  size.x = rc.right - rc.left + 24;
  size.y = rc.bottom - rc.top + 12;
  return size;
}

void ApplyTextLayer(HWND hwnd, int x, int y, int w, int h, BYTE alpha) {
  SetWindowPos(hwnd, nullptr, x, y, w, h, SWP_NOACTIVATE | SWP_SHOWWINDOW);
  SetLayeredWindowAttributes(hwnd, 0, alpha, LWA_ALPHA);
  HRGN rgn = CreateRoundRectRgn(0, 0, w, h, 8, 8);
  SetWindowRgn(hwnd, rgn, FALSE);
  InvalidateRect(hwnd, nullptr, TRUE);
}

// 暗化窗区域：整屏 − 聊天洞 − 选区洞。
void ApplyDimRegion() {
  if (!g_dim) return;
  int sw = g_screenRect.right - g_screenRect.left;
  int sh = g_screenRect.bottom - g_screenRect.top;
  HRGN full = CreateRectRgn(0, 0, sw, sh);
  RECT holes[2];
  int holeCount = 0;
  if (g_hasChatHole) holes[holeCount++] = g_chatHole;
  if (g_hasSel) holes[holeCount++] = g_selRect;
  for (int i = 0; i < holeCount; i++) {
    RECT clip;
    if (!Intersect(holes[i], g_screenRect, &clip)) continue;
    HRGN rel =
        CreateRectRgn(clip.left - g_screenRect.left, clip.top - g_screenRect.top,
                      clip.right - g_screenRect.left,
                      clip.bottom - g_screenRect.top);
    CombineRgn(full, full, rel, RGN_DIFF);
    DeleteObject(rel);
  }
  SetWindowRgn(g_dim, full, TRUE);
  InvalidateRect(g_dim, nullptr, TRUE);
}

void UpdateOverlay(bool hasSel, int x, int y, int w, int h) {
  if (!g_dim) return;
  g_hasSel = hasSel;
  if (hasSel) {
    g_selRect = RECT{x, y, x + w, y + h};
  }
  ApplyDimRegion();
  if (!hasSel) {
    ShowWindow(g_frame, SW_HIDE);
    ShowWindow(g_label, SW_HIDE);
    return;
  }
  RECT outer = FrameOuter(g_selRect);
  int ow = outer.right - outer.left, oh = outer.bottom - outer.top;
  SetWindowPos(g_frame, nullptr, outer.left, outer.top, ow, oh,
               SWP_NOACTIVATE | SWP_SHOWWINDOW);
  HRGN full = CreateRectRgn(0, 0, ow, oh);
  HRGN inner = CreateRectRgn(kBorderPx, kBorderPx,
                             kBorderPx + (g_selRect.right - g_selRect.left),
                             kBorderPx + (g_selRect.bottom - g_selRect.top));
  CombineRgn(full, full, inner, RGN_DIFF);
  DeleteObject(inner);
  SetWindowRgn(g_frame, full, TRUE);
  InvalidateRect(g_frame, nullptr, TRUE);

  wchar_t text[64];
  swprintf_s(text, L"%d × %d", g_selRect.right - g_selRect.left,
             g_selRect.bottom - g_selRect.top);
  g_labelText = text;
  POINT size = MeasureText(g_label, g_labelText);
  POINT origin = LabelOrigin(g_selRect, size.y, g_screenRect);
  ApplyTextLayer(g_label, origin.x, origin.y, size.x, size.y, kLabelAlpha);
}

void CloseOverlay() {
  if (g_dim) DestroyWindow(g_dim);
  if (g_frame) DestroyWindow(g_frame);
  if (g_label) DestroyWindow(g_label);
  if (g_hint) DestroyWindow(g_hint);
  g_dim = g_frame = g_label = g_hint = nullptr;
  g_hasChatHole = false;
  g_hasSel = false;
}

void BringChatToFront() {
  if (!g_chatHwnd || !IsWindow(g_chatHwnd)) return;
  ShowWindow(g_chatHwnd, SW_SHOW);
  SetWindowPos(g_chatHwnd, HWND_TOPMOST, 0, 0, 0, 0,
               SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
  SetForegroundWindow(g_chatHwnd);
}

void ResetCaptureState() {
  g_capturing.store(false);
  g_leftDrag.store(false);
}

// ---------- 捕获线程命令处理 ----------
void HandleBegin(int sx, int sy) {
  if (g_dim) {  // 已在截图中：复位防御
    NativeLog("begin_drag: selection already active, abort");
    ResetCaptureState();
    CloseOverlay();
    return;
  }
  if (!CaptureScreen()) {
    NativeLog("begin_drag: capture_screen failed");
    ResetCaptureState();
    return;
  }
  char buf[160];
  snprintf(buf, sizeof(buf), "begin_drag: frame %dx%d frozen, start (%d,%d)",
           g_frameW, g_frameH, sx, sy);
  NativeLog(buf);
  g_dragStartX.store(sx);
  g_dragStartY.store(sy);

  RegisterOverlayClasses();
  g_screenRect = RECT{0, 0, GetSystemMetrics(SM_CXSCREEN),
                      GetSystemMetrics(SM_CYSCREEN)};
  int sw = g_screenRect.right, sh = g_screenRect.bottom;
  g_dim = CreateLayer(L"SnapVisionSelDim",
                      WS_EX_LAYERED | WS_EX_TOPMOST | WS_EX_TOOLWINDOW |
                          WS_EX_NOACTIVATE,
                      0, 0, sw, sh);
  if (!g_dim) {
    NativeLog("begin_drag: dim window create failed");
    ResetCaptureState();
    return;
  }
  SetLayeredWindowAttributes(g_dim, 0, kDimAlpha, LWA_ALPHA);
  g_hasChatHole = false;
  if (g_chatHwnd && IsWindow(g_chatHwnd) && IsWindowVisible(g_chatHwnd)) {
    RECT rc;
    if (GetWindowRect(g_chatHwnd, &rc) &&
        Intersect(rc, g_screenRect, &g_chatHole)) {
      g_hasChatHole = true;
    }
  }
  ShowWindow(g_dim, SW_SHOWNOACTIVATE);
  ApplyDimRegion();

  g_frame = CreateLayer(L"SnapVisionSelFrame",
                        WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE |
                            WS_EX_TRANSPARENT,
                        0, 0, 1, 1);
  g_label = CreateLayer(L"SnapVisionSelLayer",
                        WS_EX_LAYERED | WS_EX_TOPMOST | WS_EX_TOOLWINDOW |
                            WS_EX_NOACTIVATE | WS_EX_TRANSPARENT,
                        0, 0, 1, 1);
  g_hint = CreateLayer(L"SnapVisionSelLayer",
                       WS_EX_LAYERED | WS_EX_TOPMOST | WS_EX_TOOLWINDOW |
                           WS_EX_NOACTIVATE | WS_EX_TRANSPARENT,
                       0, 0, 1, 1);
  if (!g_frame || !g_label || !g_hint) {
    NativeLog("begin_drag: overlay layer create failed");
    CloseOverlay();
    ResetCaptureState();
    return;
  }
  ShowWindow(g_frame, SW_HIDE);
  ShowWindow(g_label, SW_HIDE);
  POINT hintSize = MeasureText(g_hint, kHintText);
  ApplyTextLayer(g_hint, (sw - hintSize.x) / 2, 36, hintSize.x, hintSize.y,
                 kHintAlpha);
  NativeLog("selection: native overlay begun (chat never occluded)");
  UpdateOverlay(false, 0, 0, 0, 0);
}

void HandleCancel() {
  NativeLog("selection: cancelled");
  CloseOverlay();
  BringChatToFront();
  ResetCaptureState();
}

void HandleEnd(int ex, int ey) {
  int sx = g_dragStartX.load();
  int sy = g_dragStartY.load();
  int x = sx < ex ? sx : ex;
  int y = sy < ey ? sy : ey;
  int w = ex > sx ? ex - sx : sx - ex;
  int h = ey > sy ? ey - sy : sy - ey;
  char buf[96];
  snprintf(buf, sizeof(buf), "end_drag: rect (%d,%d) %dx%d", x, y, w, h);
  NativeLog(buf);
  if (w < 8 || h < 8) {
    NativeLog("end_drag: too small, cancel");
    HandleCancel();
    return;
  }
  int64_t capturedAt = (int64_t)NowMs();
  CapturePayload payload;
  bool ok = CropRgba(x, y, w, h, &payload);
  const char* status = ok ? "ok" : "crop failed";
  snprintf(buf, sizeof(buf), "end_drag: crop %s", status);
  NativeLog(buf);
  CloseOverlay();
  BringChatToFront();
  ResetCaptureState();
  if (ok && g_callback) {
    payload.capturedAtMs = capturedAt;
    g_callback(std::move(payload));
  }
}

void HandleMove(int mx, int my) {
  int sx = g_dragStartX.load();
  int sy = g_dragStartY.load();
  int x = sx < mx ? sx : mx;
  int y = sy < my ? sy : my;
  int w = mx > sx ? mx - sx : sx - mx;
  int h = my > sy ? my - sy : sy - my;
  UpdateOverlay(w >= 1 && h >= 1, x, y, w, h);
}

DWORD WINAPI CaptureThreadProc(LPVOID) {
  g_captureThreadId = GetCurrentThreadId();
  // 消息队列建立后放行钩子线程，避免 PostThreadMessage 打到空队列
  if (g_captureReady) SetEvent(g_captureReady);
  NativeLog("=== snap vision native capture thread ready ===");
  MSG msg;
  while (GetMessage(&msg, nullptr, 0, 0) > 0) {
    switch (msg.message) {
      case kMsgBegin:
        HandleBegin((int)msg.wParam, (int)msg.lParam);
        break;
      case kMsgMove:
        HandleMove((int)msg.wParam, (int)msg.lParam);
        break;
      case kMsgEnd:
        HandleEnd((int)msg.wParam, (int)msg.lParam);
        break;
      case kMsgCancel:
        HandleCancel();
        break;
      default:
        break;  // 窗口消息由 DispatchMessage 派发——但本线程窗口消息会进入
                // GetMessage；选框窗的 WM_PAINT 需要 Dispatch。
    }
    // 窗口消息（选框层）走默认派发
    TranslateMessage(&msg);
    DispatchMessage(&msg);
  }
  return 0;
}

// ---------- 低级钩子（钩子线程回调：只做判定与投递，立即返回） ----------
LRESULT CALLBACK MouseProc(int ncode, WPARAM wparam, LPARAM lparam) {
  if (ncode >= 0) {
    const MSLLHOOKSTRUCT* info = (const MSLLHOOKSTRUCT*)lparam;
    int mx = info->pt.x, my = info->pt.y;
    bool altDown = (GetAsyncKeyState(VK_MENU) & 0x8000) != 0;
    bool capturing = g_capturing.load();
    switch (wparam) {
      case WM_RBUTTONDOWN:
        if (altDown && !capturing) {
          bool expected = false;
          if (g_capturing.compare_exchange_strong(expected, true)) {
            char buf[80];
            snprintf(buf, sizeof(buf), "hook: Alt+RBDown at (%d,%d)", mx, my);
            NativeLog(buf);
            PostThreadMessage(g_captureThreadId, kMsgBegin, (WPARAM)mx,
                              (LPARAM)my);
            return 1;  // 吞掉，防右键菜单
          }
          return 1;
        }
        if (capturing) {
          NativeLog("hook: RBDown during capture, cancel");
          PostThreadMessage(g_captureThreadId, kMsgCancel, 0, 0);
          return 1;
        }
        break;
      case WM_RBUTTONUP:
        if (capturing && !g_leftDrag.load()) {
          char buf[80];
          snprintf(buf, sizeof(buf), "hook: RBUp at (%d,%d)", mx, my);
          NativeLog(buf);
          PostThreadMessage(g_captureThreadId, kMsgEnd, (WPARAM)mx, (LPARAM)my);
          return 1;
        }
        if (altDown) return 1;  // 会话外 Alt+右键仍吞掉
        break;
      case WM_LBUTTONDOWN:
        if (capturing) {
          char buf[80];
          snprintf(buf, sizeof(buf), "hook: LDown backup drag at (%d,%d)", mx,
                   my);
          NativeLog(buf);
          g_leftDrag.store(true);
          g_dragStartX.store(mx);
          g_dragStartY.store(my);
          return 1;
        }
        break;
      case WM_LBUTTONUP:
        if (capturing && g_leftDrag.load()) {
          char buf[80];
          snprintf(buf, sizeof(buf), "hook: LUp at (%d,%d)", mx, my);
          NativeLog(buf);
          PostThreadMessage(g_captureThreadId, kMsgEnd, (WPARAM)mx, (LPARAM)my);
          return 1;
        }
        break;
      case WM_MOUSEMOVE:
        if (capturing) {
          uint64_t now = NowMs();
          uint64_t last = g_lastMoveMs.load();
          if (now - last >= 15 &&
              g_lastMoveMs.compare_exchange_strong(last, now)) {
            PostThreadMessage(g_captureThreadId, kMsgMove, (WPARAM)mx,
                              (LPARAM)my);
          }
        }
        break;
      default:
        break;
    }
  }
  return CallNextHookEx(nullptr, ncode, wparam, lparam);
}

LRESULT CALLBACK KeyboardProc(int ncode, WPARAM wparam, LPARAM lparam) {
  if (ncode >= 0 && wparam == WM_KEYDOWN && g_capturing.load()) {
    const KBDLLHOOKSTRUCT* kb = (const KBDLLHOOKSTRUCT*)lparam;
    if (kb->vkCode == VK_ESCAPE) {
      PostThreadMessage(g_captureThreadId, kMsgCancel, 0, 0);
      return 1;
    }
  }
  return CallNextHookEx(nullptr, ncode, wparam, lparam);
}

DWORD WINAPI HookThreadProc(LPVOID) {
  HHOOK mouseHook =
      SetWindowsHookExW(WH_MOUSE_LL, MouseProc, nullptr, 0);
  if (!mouseHook) {
    NativeLog("mouse hook install FAILED");
    return 1;
  }
  HHOOK kbHook = SetWindowsHookExW(WH_KEYBOARD_LL, KeyboardProc, nullptr, 0);
  if (!kbHook) {
    NativeLog("keyboard hook install FAILED");
    UnhookWindowsHookEx(mouseHook);
    return 1;
  }
  NativeLog("mouse + keyboard hooks installed");
  g_mouseHook = mouseHook;
  g_kbHook = kbHook;
  MSG msg;
  while (GetMessage(&msg, nullptr, 0, 0) > 0) {
    TranslateMessage(&msg);
    DispatchMessage(&msg);
  }
  UnhookWindowsHookEx(mouseHook);
  UnhookWindowsHookEx(kbHook);
  return 0;
}

}  // namespace

void InitCaptureModule(HWND chatHwnd, CaptureCallback callback) {
  g_chatHwnd = chatHwnd;
  g_callback = std::move(callback);
  g_captureReady = CreateEvent(nullptr, TRUE, FALSE, nullptr);
  CreateThread(nullptr, 0, CaptureThreadProc, nullptr, 0, nullptr);
  // 等捕获线程就绪（最多 2s），再装钩子
  if (g_captureReady) WaitForSingleObject(g_captureReady, 2000);
  CreateThread(nullptr, 0, HookThreadProc, nullptr, 0, nullptr);
}

}  // namespace snapvision

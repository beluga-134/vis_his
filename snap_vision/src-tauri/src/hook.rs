//! 全局低级鼠标钩子：Alt+右键按下直接进入拖拽截屏，松开右键完成。
//! 坐标全程物理像素（与冻结帧同坐标系），不经过页面换算。

use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::{Mutex, OnceLock};

use tauri::{AppHandle, Emitter, Manager, WebviewUrl, WebviewWindowBuilder};

use crate::capture::{self, Frame};

pub static CAPTURING: AtomicBool = AtomicBool::new(false);
pub static OVERLAY_READY: AtomicBool = AtomicBool::new(false);
pub static FRAME: Mutex<Option<Frame>> = Mutex::new(None);
static HOOK_APP: OnceLock<AppHandle> = OnceLock::new();
static DRAG_START: Mutex<Option<(i32, i32)>> = Mutex::new(None);
static LAST_EMIT_MS: AtomicU64 = AtomicU64::new(0);

/// 简单追加日志：%TEMP%\snap_vision\app.log
pub fn log(msg: &str) {
    let dir = std::env::temp_dir().join("snap_vision");
    let _ = std::fs::create_dir_all(&dir);
    let ts = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map(|d| d.as_millis())
        .unwrap_or(0);
    use std::io::Write;
    let _ = std::fs::OpenOptions::new()
        .create(true)
        .append(true)
        .open(dir.join("app.log"))
        .and_then(|mut f| f.write_all(format!("{ts} {msg}\n").as_bytes()));
}

pub fn current_app() -> Option<AppHandle> {
    HOOK_APP.get().cloned()
}

pub fn reset_capture_state() {
    CAPTURING.store(false, Ordering::SeqCst);
    let _ = DRAG_START.lock().map(|mut d| d.take());
    let _ = FRAME.lock().map(|mut f| f.take());
}

pub fn close_overlay(app: &AppHandle) {
    if let Some(overlay) = app.get_webview_window("overlay") {
        let _ = overlay.close();
    }
}

fn now_ms() -> u64 {
    std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map(|d| d.as_millis() as u64)
        .unwrap_or(0)
}

/// 裁剪并存盘（含全链路计时锚点）。
pub fn crop_save_with_time(x: i32, y: i32, w: i32, h: i32, captured_at_ms: u64) -> Result<CaptureOutcome, String> {
    let mut outcome = crop_save(x, y, w, h)?;
    outcome.captured_at_ms = captured_at_ms;
    Ok(outcome)
}

/// 裁剪冻结帧并存盘，返回 PNG 路径 + 缩略图 data URL（阻塞，供任意线程调用）。
pub fn crop_save(x: i32, y: i32, w: i32, h: i32) -> Result<CaptureOutcome, String> {
    let frame = FRAME
        .lock()
        .map_err(|_| "内部状态锁损坏")?
        .clone()
        .ok_or("截屏帧不存在")?;
    let (png, thumb) = capture::crop_png_and_thumb(&frame, x as f64, y as f64, w as f64, h as f64, 480)?;
    let dir = std::env::temp_dir().join("snap_vision");
    std::fs::create_dir_all(&dir).map_err(|e| format!("创建目录失败：{e}"))?;
    let ts = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map(|d| d.as_millis())
        .unwrap_or(0);
    let path = dir.join(format!("cap-{ts}.png"));
    std::fs::write(&path, &png).map_err(|e| format!("写入截图失败：{e}"))?;
    Ok(CaptureOutcome {
        path: path.to_string_lossy().into_owned(),
        thumb,
        captured_at_ms: 0,
    })
}

#[derive(Clone, serde::Serialize)]
#[serde(rename_all = "camelCase")]
pub struct CaptureOutcome {
    pub path: String,
    pub thumb: String,
    /// 松开右键的时刻（unix ms），供前端计算全链路耗时
    #[serde(rename = "capturedAt")]
    pub captured_at_ms: u64,
}

/// 裁剪完成后：关 overlay、复位、唤出聊天窗、广播事件。必须在主线程。
pub fn dispatch_result(app: &AppHandle, result: &Result<CaptureOutcome, String>) {
    close_overlay(app);
    reset_capture_state();
    if let Ok(outcome) = result {
        if let Some(chat) = app.get_webview_window("chat") {
            let _ = chat.show();
            let _ = chat.set_focus();
        }
        let payload = serde_json::json!({
            "path": outcome.path,
            "thumb": outcome.thumb,
            "capturedAt": outcome.captured_at_ms,
        });
        let emit_result = app.emit("vision://new-capture", payload);
        if let Err(err) = emit_result {
            log(&format!("emit vision://new-capture FAILED: {err}"));
        } else {
            log(&format!("emitted vision://new-capture: {}", outcome.path));
        }
    }
}

#[cfg(target_os = "windows")]
unsafe extern "system" fn mouse_proc(ncode: i32, wparam: usize, lparam: isize) -> isize {
    use windows_sys::Win32::Foundation::POINT;
    use windows_sys::Win32::UI::Input::KeyboardAndMouse::GetAsyncKeyState;
    use windows_sys::Win32::UI::WindowsAndMessaging::{
        CallNextHookEx, WM_MOUSEMOVE, WM_RBUTTONDOWN, WM_RBUTTONUP,
    };

    if ncode >= 0 {
        let msg = wparam as u32;
        // MSLLHOOKSTRUCT.pt 是物理屏幕坐标，与冻结帧同坐标系
        let info_pt = *(lparam as *const POINT);
        let (mx, my) = (info_pt.x, info_pt.y);
        let alt_down = (GetAsyncKeyState(0x12 /* VK_MENU */) as u16 & 0x8000) != 0;
        let capturing = CAPTURING.load(Ordering::SeqCst);

        match msg {
            WM_RBUTTONDOWN if alt_down && !capturing => {
                if !CAPTURING.swap(true, Ordering::SeqCst) {
                    log(&format!("hook: Alt+RBDown at ({mx},{my})"));
                    if let Some(app) = HOOK_APP.get().cloned() {
                        let app_task = app.clone();
                        let sx = mx;
                        let sy = my;
                        let dispatched =
                            app.run_on_main_thread(move || begin_drag(app_task, sx, sy));
                        if dispatched.is_err() {
                            log("hook: dispatch begin_drag failed");
                            reset_capture_state();
                        }
                    } else {
                        reset_capture_state();
                    }
                }
                return 1; // 吞掉，防右键菜单
            }
            WM_RBUTTONUP if capturing => {
                log(&format!("hook: RBUp at ({mx},{my})"));
                if let Some(app) = HOOK_APP.get().cloned() {
                    let app_task = app.clone();
                    let ex = mx;
                    let ey = my;
                    let _ = app.run_on_main_thread(move || end_drag(app_task, ex, ey));
                }
                return 1; // 吞掉，防菜单
            }
            WM_MOUSEMOVE if capturing => {
                // 节流转发给 overlay 画选框（物理像素）
                let now = now_ms();
                if now.saturating_sub(LAST_EMIT_MS.load(Ordering::Relaxed)) >= 15 {
                    LAST_EMIT_MS.store(now, Ordering::Relaxed);
                    if let Some(app) = HOOK_APP.get() {
                        if let Some(overlay) = app.get_webview_window("overlay") {
                            if let Some((sx, sy)) = *DRAG_START.lock().unwrap_or_else(|e| e.into_inner()) {
                                let rect = serde_json::json!({
                                    "x": sx.min(mx),
                                    "y": sy.min(my),
                                    "w": (mx - sx).abs(),
                                    "h": (my - sy).abs(),
                                });
                                let _ = overlay.emit("vision://drag", rect);
                            }
                        }
                    }
                }
                // 移动事件不吞
                return CallNextHookEx(std::ptr::null_mut(), ncode, wparam, lparam);
            }
            // 拖选会话之外，Alt+右键仍然吞掉（避免误弹菜单）
            WM_RBUTTONDOWN | WM_RBUTTONUP if alt_down => return 1,
            _ => {}
        }
    }
    CallNextHookEx(std::ptr::null_mut(), ncode, wparam, lparam)
}

/// Alt+右键按下：冻结屏幕、记录起点、弹出 overlay。主线程。
fn begin_drag(app: AppHandle, sx: i32, sy: i32) {
    if app.get_webview_window("overlay").is_some() {
        log("begin_drag: overlay already exists, abort");
        reset_capture_state();
        return;
    }
    match capture::capture_screen() {
        Ok(frame) => {
            let (w, h) = (frame.width, frame.height);
            *FRAME.lock().unwrap_or_else(|e| e.into_inner()) = Some(frame);
            *DRAG_START.lock().unwrap_or_else(|e| e.into_inner()) = Some((sx, sy));
            log(&format!("begin_drag: frame {w}x{h} frozen, start ({sx},{sy})"));
            let built = WebviewWindowBuilder::new(
                &app,
                "overlay",
                WebviewUrl::App("overlay.html".into()),
            )
            .title("Snap Vision 截图")
            .decorations(false)
            .fullscreen(true)
            .always_on_top(true)
            .skip_taskbar(true)
            .resizable(false)
            .visible(false)
            .transparent(true)
            .build();
            match built {
                Ok(_) => {
                    log("begin_drag: overlay built");
                    OVERLAY_READY.store(false, Ordering::SeqCst);
                    // 看门狗：页面 5 秒未报活自动关窗，避免锁屏
                    std::thread::spawn(move || {
                        std::thread::sleep(std::time::Duration::from_secs(5));
                        if !OVERLAY_READY.load(Ordering::SeqCst) {
                            log("watchdog: overlay not ready in 5s, closing");
                            let _ = app.run_on_main_thread(move || {
                                if let Some(a) = current_app() {
                                    close_overlay(&a);
                                    reset_capture_state();
                                }
                            });
                        }
                    });
                }
                Err(err) => {
                    log(&format!("begin_drag: overlay build failed: {err}"));
                    reset_capture_state();
                }
            }
        }
        Err(err) => {
            log(&format!("begin_drag: capture_screen failed: {err}"));
            reset_capture_state();
        }
    }
}

/// 右键松开：归一化矩形，太小视为取消；否则后台裁剪后回主线程派发。主线程进入。
fn end_drag(app: AppHandle, ex: i32, ey: i32) {
    let start = DRAG_START
        .lock()
        .unwrap_or_else(|e| e.into_inner())
        .take();
    let Some((sx, sy)) = start else {
        log("end_drag: no start point, reset");
        close_overlay(&app);
        reset_capture_state();
        return;
    };
    let (x, y, w, h) = (sx.min(ex), sy.min(ey), (ex - sx).abs(), (ey - sy).abs());
    log(&format!("end_drag: rect ({x},{y}) {w}x{h}"));
    if w < 8 || h < 8 {
        log("end_drag: too small, cancel");
        close_overlay(&app);
        reset_capture_state();
        return;
    }
    let captured_at_ms = now_ms();
    let app_task = app.clone();
    std::thread::spawn(move || {
        let result = crop_save_with_time(x, y, w, h, captured_at_ms);
        log(&format!(
            "end_drag: crop result {:?}",
            result.as_ref().map(|o| o.path.as_str())
        ));
        let app_main = app_task.clone();
        let _ = app_task.run_on_main_thread(move || {
            dispatch_result(&app_main, &result);
        });
    });
}

pub fn install(app: AppHandle) {
    HOOK_APP.set(app).ok();
    log("=== snap vision starting, hook install ===");
    #[cfg(target_os = "windows")]
    std::thread::spawn(|| unsafe {
        use windows_sys::Win32::UI::WindowsAndMessaging::{
            GetMessageW, SetWindowsHookExW, UnhookWindowsHookEx, MSG, WH_MOUSE_LL,
        };
        let hook = SetWindowsHookExW(WH_MOUSE_LL, Some(mouse_proc), std::ptr::null_mut(), 0);
        if hook.is_null() {
            log("hook install FAILED");
            eprintln!("鼠标钩子安装失败，Alt+右键将不可用");
            return;
        }
        log("hook installed");
        let mut msg: MSG = std::mem::zeroed();
        while GetMessageW(&mut msg, std::ptr::null_mut(), 0, 0) != 0 {}
        let _ = UnhookWindowsHookEx(hook);
    });
}

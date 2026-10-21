mod capture;
mod hook;
mod ocr;
mod vision;

use base64::Engine;
use serde::Deserialize;
use tauri::Manager;

/// 左键备用路径：overlay 页面拖框完成后调用（主路径已移入钩子，Alt+右键直接拖选）。
#[tauri::command]
fn finish_capture(x: f64, y: f64, w: f64, h: f64) -> Result<hook::CaptureOutcome, String> {
    hook::log(&format!("command finish_capture({x:.0},{y:.0},{w:.0},{h:.0})"));
    let result = hook::crop_save(x as i32, y as i32, w as i32, h as i32);
    if let Some(app) = hook::current_app() {
        hook::dispatch_result(&app, &result);
    }
    result
}

/// 取消本次截图（Esc / 右键 / 误触）。
#[tauri::command]
fn cancel_capture() {
    hook::log("command cancel_capture");
    if let Some(app) = hook::current_app() {
        hook::close_overlay(&app);
    }
    hook::reset_capture_state();
}

/// overlay 页面就绪：置位看门狗标志并显示窗口。若此刻拖选已结束（竞态），不再显示。
#[tauri::command]
fn overlay_ready() {
    hook::OVERLAY_READY.store(true, std::sync::atomic::Ordering::SeqCst);
    if !hook::CAPTURING.load(std::sync::atomic::Ordering::SeqCst) {
        hook::log("overlay_ready: capturing already finished, skip show");
        return;
    }
    if let Some(app) = hook::current_app() {
        if let Some(overlay) = app.get_webview_window("overlay") {
            let _ = overlay.show();
            let _ = overlay.set_focus();
        }
    }
}

/// 供 overlay 页面显示冻结背景（整帧 PNG 的 data URL）。
#[tauri::command(async)]
fn get_capture_frame() -> Result<String, String> {
    let frame = hook::FRAME
        .lock()
        .map_err(|_| "内部状态锁损坏".to_string())?
        .clone()
        .ok_or("截屏帧不存在")?;
    let png = capture::frame_to_png(&frame)?;
    let encoded = base64::engine::general_purpose::STANDARD.encode(&png);
    Ok(format!("data:image/png;base64,{encoded}"))
}

/// 缩略图显示用（最长边 720）。
#[tauri::command(async)]
fn read_image_data_url(path: String) -> Result<String, String> {
    capture::file_to_data_url(&path, 720)
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct ChatMessageDto {
    role: String,
    text: String,
    image_path: Option<String>,
}

/// 本地 RapidOCR：返回行列对齐文本与耗时。
#[derive(serde::Serialize)]
#[serde(rename_all = "camelCase")]
struct OcrLocalResult {
    text: String,
    lines: Vec<ocr::OcrLine>,
    elapsed_ms: u64,
    device: String,
}

#[tauri::command(async)]
async fn ocr_local(image_path: String) -> Result<OcrLocalResult, String> {
    hook::log(&format!("ocr_local: {image_path}"));
    let path = image_path.clone();
    let (text, lines, elapsed_ms, device) =
        tauri::async_runtime::spawn_blocking(move || ocr::run(&path))
            .await
            .map_err(|e| format!("任务调度失败：{e}"))??;
    hook::log(&format!(
        "ocr_local: {} lines in {elapsed_ms}ms on {device}",
        lines.len()
    ));
    Ok(OcrLocalResult {
        text,
        lines,
        elapsed_ms,
        device,
    })
}

/// 多轮对话：历史消息 +（首条 user 携带）图片 → glm-5.3-flash。
#[tauri::command(async)]
async fn vision_chat(messages: Vec<ChatMessageDto>) -> Result<String, String> {
    hook::log(&format!(
        "vision_chat: {} turns, image={:?}",
        messages.len(),
        messages.iter().find_map(|m| m.image_path.clone())
    ));
    let mut turns = Vec::with_capacity(messages.len());
    for msg in &messages {
        let image_data_url = match &msg.image_path {
            Some(path) => Some(capture::file_to_data_url(path, 1600)?),
            None => None,
        };
        turns.push(vision::ChatTurn {
            role: msg.role.clone(),
            text: msg.text.clone(),
            image_data_url,
        });
    }
    tauri::async_runtime::spawn_blocking(move || vision::chat(&turns))
        .await
        .map_err(|e| format!("任务调度失败：{e}"))?
}

pub fn run() {
    tauri::Builder::default()
        .setup(|app| {
            // Win11：把聊天窗裁成系统级圆角（含 acrylic 材质），消除四角方形留白
            #[cfg(target_os = "windows")]
            if let Some(chat) = app.get_webview_window("chat") {
                if let Ok(hwnd) = chat.hwnd() {
                    unsafe {
                        const DWMWA_WINDOW_CORNER_PREFERENCE: u32 = 33;
                        const DWMWCP_ROUND: u32 = 2;
                        let pref: u32 = DWMWCP_ROUND;
                        let _ = windows_sys::Win32::Graphics::Dwm::DwmSetWindowAttribute(
                            hwnd.0 as usize as windows_sys::Win32::Foundation::HWND,
                            DWMWA_WINDOW_CORNER_PREFERENCE,
                            &pref as *const u32 as *const core::ffi::c_void,
                            std::mem::size_of::<u32>() as u32,
                        );
                    }
                }
            }
            hook::install(app.handle().clone());
            Ok(())
        })
        .on_window_event(|window, event| {
            // overlay 以任何方式销毁（含 Alt+F4）都复位状态，避免卡死在“截图中”
            if window.label() == "overlay" {
                if let tauri::WindowEvent::Destroyed = event {
                    hook::reset_capture_state();
                }
            }
        })
        .invoke_handler(tauri::generate_handler![
            finish_capture,
            cancel_capture,
            overlay_ready,
            get_capture_frame,
            read_image_data_url,
            vision_chat,
            ocr_local
        ])
        .run(tauri::generate_context!())
        .expect("error while running Snap Vision");
}

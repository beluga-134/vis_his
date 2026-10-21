//! 屏幕捕获与 PNG 裁剪（Windows BitBlt 主屏；其他平台返回错误）。

#[derive(Clone)]
pub struct Frame {
    pub width: i32,
    pub height: i32,
    /// 从上到下、从左到右的 BGRA 像素
    pub bgra: Vec<u8>,
}

/// 抓取主屏整帧（物理像素）。
pub fn capture_screen() -> Result<Frame, String> {
    #[cfg(target_os = "windows")]
    {
        unsafe {
            use windows_sys::Win32::Graphics::Gdi::{
                BitBlt, CreateCompatibleBitmap, CreateCompatibleDC, DeleteDC, DeleteObject,
                GetDC, GetDIBits, ReleaseDC, SelectObject, BITMAPINFO, BITMAPINFOHEADER, BI_RGB,
                DIB_RGB_COLORS, SRCCOPY,
            };
            use windows_sys::Win32::UI::WindowsAndMessaging::{
                GetSystemMetrics, SM_CXSCREEN, SM_CYSCREEN,
            };

            let screen_dc = GetDC(std::ptr::null_mut());
            if screen_dc.is_null() {
                return Err("无法获取屏幕 DC".into());
            }
            let width = GetSystemMetrics(SM_CXSCREEN);
            let height = GetSystemMetrics(SM_CYSCREEN);
            if width <= 0 || height <= 0 {
                ReleaseDC(std::ptr::null_mut(), screen_dc);
                return Err(format!("屏幕尺寸异常：{width}x{height}"));
            }

            let mem_dc = CreateCompatibleDC(screen_dc);
            let bitmap = CreateCompatibleBitmap(screen_dc, width, height);
            let old_obj = SelectObject(mem_dc, bitmap as *mut core::ffi::c_void);
            let blit_ok = BitBlt(
                mem_dc,
                0,
                0,
                width,
                height,
                screen_dc,
                0,
                0,
                SRCCOPY, // 不用 CAPTUREBLT：避免层叠捕获导致的屏幕/光标闪烁
            );
            if blit_ok == 0 {
                SelectObject(mem_dc, old_obj);
                DeleteObject(bitmap as *mut core::ffi::c_void);
                DeleteDC(mem_dc);
                ReleaseDC(std::ptr::null_mut(), screen_dc);
                return Err("BitBlt 截屏失败".into());
            }

            let mut info: BITMAPINFO = std::mem::zeroed();
            info.bmiHeader.biSize = std::mem::size_of::<BITMAPINFOHEADER>() as u32;
            info.bmiHeader.biWidth = width;
            info.bmiHeader.biHeight = -height; // top-down
            info.bmiHeader.biPlanes = 1;
            info.bmiHeader.biBitCount = 32;
            info.bmiHeader.biCompression = BI_RGB;

            let mut buf = vec![0u8; (width as usize) * (height as usize) * 4];
            let lines = GetDIBits(
                mem_dc,
                bitmap,
                0,
                height as u32,
                buf.as_mut_ptr().cast(),
                &mut info,
                DIB_RGB_COLORS,
            );

            SelectObject(mem_dc, old_obj);
            DeleteObject(bitmap as *mut core::ffi::c_void);
            DeleteDC(mem_dc);
            ReleaseDC(std::ptr::null_mut(), screen_dc);

            if lines != height {
                return Err(format!("GetDIBits 失败：仅取得 {lines} 行（期望 {height}）"));
            }
            Ok(Frame { width, height, bgra: buf })
        }
    }
    #[cfg(not(target_os = "windows"))]
    {
        Err("截屏目前仅支持 Windows".into())
    }
}

fn bgra_to_rgba_image(frame: &Frame, x: i32, y: i32, w: i32, h: i32) -> image::RgbaImage {
    // 批量行拷贝 + 逐像素仅交换通道，避免 put_pixel 的逐点索引开销
    let mut data = vec![0u8; (w as usize) * (h as usize) * 4];
    for row in 0..h as usize {
        let src = ((y as usize + row) * frame.width as usize + x as usize) * 4;
        let dst = row * w as usize * 4;
        let len = w as usize * 4;
        data[dst..dst + len].copy_from_slice(&frame.bgra[src..src + len]);
    }
    for px in data.chunks_exact_mut(4) {
        px.swap(0, 2); // BGRA -> RGBA
    }
    image::RgbaImage::from_raw(w as u32, h as u32, data).expect("尺寸已校验")
}

/// 快速 PNG：Fast 压缩 + 无滤波，比默认编码快 2–5×（文件稍大，OCR/GLM 不敏感）
fn encode_png_fast(img: &image::RgbaImage) -> Result<Vec<u8>, String> {
    use image::codecs::png::{CompressionType, FilterType, PngEncoder};
    use image::ImageEncoder;
    let mut out = Vec::new();
    let (w, h) = img.dimensions();
    let enc = PngEncoder::new_with_quality(&mut out, CompressionType::Fast, FilterType::NoFilter);
    enc.write_image(img.as_raw(), w, h, image::ExtendedColorType::Rgba8)
        .map_err(|e| format!("PNG 编码失败：{e}"))?;
    Ok(out)
}

/// base64 编码后体积上限（与 glm-vision 扩展的 4.5MiB 约定一致）。
pub const MAX_ENCODED_BYTES: usize = 4_718_592;

fn encode_png_under_limit(mut img: image::RgbaImage) -> Result<Vec<u8>, String> {
    let mut png = encode_png_fast(&img)?;
    // 超限时等比缩到长边 1600 再试一次
    if png.len() * 4 / 3 > MAX_ENCODED_BYTES {
        let (w, h) = img.dimensions();
        let long = w.max(h).max(1);
        let scale = 1600f64 / long as f64;
        let nw = ((w as f64 * scale).round() as u32).max(1);
        let nh = ((h as f64 * scale).round() as u32).max(1);
        img = image::imageops::resize(&img, nw, nh, image::imageops::FilterType::Triangle);
        png = encode_png_fast(&img)?;
    }
    Ok(png)
}

fn clamp_rect(frame: &Frame, x: f64, y: f64, w: f64, h: f64) -> Result<(i32, i32, i32, i32), String> {
    let x0 = (x.round() as i32).max(0);
    let y0 = (y.round() as i32).max(0);
    let x1 = ((x + w).round() as i32).min(frame.width);
    let y1 = ((y + h).round() as i32).min(frame.height);
    let cw = x1 - x0;
    let ch = y1 - y0;
    if cw < 1 || ch < 1 {
        return Err(format!("裁剪区域无效：{cw}x{ch}"));
    }
    Ok((x0, y0, cw, ch))
}

/// 把冻结帧裁剪为 PNG（物理像素坐标，内部 clamp 到帧边界）。
pub fn crop_to_png(frame: &Frame, x: f64, y: f64, w: f64, h: f64) -> Result<Vec<u8>, String> {
    let (x0, y0, cw, ch) = clamp_rect(frame, x, y, w, h)?;
    let img = bgra_to_rgba_image(frame, x0, y0, cw, ch);
    encode_png_under_limit(img)
}

/// 裁剪并同时产出缩略图 data URL（长边 thumb_edge），省去前端二次解码。
pub fn crop_png_and_thumb(
    frame: &Frame,
    x: f64,
    y: f64,
    w: f64,
    h: f64,
    thumb_edge: u32,
) -> Result<(Vec<u8>, String), String> {
    let (x0, y0, cw, ch) = clamp_rect(frame, x, y, w, h)?;
    let img = bgra_to_rgba_image(frame, x0, y0, cw, ch);
    let png = encode_png_under_limit(img.clone())?;

    // 缩略图：从像素直接缩，不经过解码（克隆一份避免移动后借用）
    let (iw, ih) = img.dimensions();
    let mut thumb = img.clone();
    let long = iw.max(ih);
    if long > thumb_edge {
        let scale = thumb_edge as f64 / long as f64;
        let nw = ((iw as f64 * scale).round() as u32).max(1);
        let nh = ((ih as f64 * scale).round() as u32).max(1);
        thumb = image::imageops::resize(&img, nw, nh, image::imageops::FilterType::Triangle);
    }
    let thumb_png = encode_png_fast(&thumb)?;
    use base64::Engine;
    let encoded = base64::engine::general_purpose::STANDARD.encode(&thumb_png);
    Ok((png, format!("data:image/png;base64,{encoded}")))
}

/// 整帧转 PNG（兼容旧路径）。
pub fn frame_to_png(frame: &Frame) -> Result<Vec<u8>, String> {
    let img = bgra_to_rgba_image(frame, 0, 0, frame.width, frame.height);
    encode_png_fast(&img)
}

/// 读取图片文件并转成 data URL；超过体积限制时先等比缩小。
/// `max_edge` 为最长边上限（送模型 1600）。
pub fn file_to_data_url(path: &str, max_edge: u32) -> Result<String, String> {
    let img = image::open(path).map_err(|e| format!("无法解码图片 {path}：{e}"))?;
    let mut img = img.to_rgba8();
    let (w, h) = img.dimensions();
    let long = w.max(h);
    if long > max_edge {
        let scale = max_edge as f64 / long as f64;
        let nw = ((w as f64 * scale).round() as u32).max(1);
        let nh = ((h as f64 * scale).round() as u32).max(1);
        img = image::imageops::resize(&img, nw, nh, image::imageops::FilterType::Triangle);
    }
    let png = encode_png_under_limit(img)?;
    use base64::Engine;
    let encoded = base64::engine::general_purpose::STANDARD.encode(&png);
    Ok(format!("data:image/png;base64,{encoded}"))
}

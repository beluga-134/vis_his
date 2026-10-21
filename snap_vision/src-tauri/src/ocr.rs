//! RapidOCR 本地引擎：常驻 Python 子进程（stdin/stdout 行 JSON）。

use std::io::{BufRead, BufReader, Read, Write};
use std::process::{Child, ChildStdin, ChildStdout, Command, Stdio};
use std::sync::Mutex;
use std::time::Duration;

use serde::Deserialize;

#[derive(Clone, Debug, Deserialize, serde::Serialize)]
pub struct OcrLine {
    pub text: String,
    pub score: f64,
}

#[derive(Deserialize)]
struct OcrResponse {
    pub ok: bool,
    #[serde(default)]
    pub lines: Vec<OcrLine>,
    #[serde(default)]
    pub layout: String,
    #[serde(default)]
    pub elapsed_ms: u64,
    #[serde(default)]
    pub device: String,
    #[serde(default)]
    pub error: String,
}

struct OcrProc {
    child: Child,
    stdin: ChildStdin,
    reader: BufReader<ChildStdout>,
}

static OCR_PROC: Mutex<Option<OcrProc>> = Mutex::new(None);

/// exe 同目录及向上两级内查找 .venv-ocr 与 scripts/ocr_server.py。
fn locate_runtime() -> Option<(std::path::PathBuf, std::path::PathBuf)> {
    let exe = std::env::current_exe().ok()?;
    let mut dir = exe.parent()?.to_path_buf();
    for _ in 0..3 {
        let py = dir.join(".venv-ocr").join("Scripts").join("python.exe");
        let script = dir.join("scripts").join("ocr_server.py");
        if py.is_file() && script.is_file() {
            return Some((py, script));
        }
        if !dir.pop() {
            break;
        }
    }
    None
}

fn spawn_process() -> Result<OcrProc, String> {
    let (py, script) = locate_runtime().ok_or(
        "未找到 .venv-ocr/Scripts/python.exe 或 scripts/ocr_server.py（exe 向上三层内）",
    )?;
    let mut cmd = Command::new(&py);
    cmd.arg(&script)
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .stderr(Stdio::null());
    #[cfg(target_os = "windows")]
    {
        use std::os::windows::process::CommandExt;
        cmd.creation_flags(0x0800_0000); // CREATE_NO_WINDOW
    }
    let mut child = cmd.spawn().map_err(|e| format!("启动 OCR 进程失败：{e}"))?;
    let stdin = child.stdin.take().ok_or("无法取得 OCR 子进程 stdin")?;
    let stdout = child.stdout.take().ok_or("无法取得 OCR 子进程 stdout")?;
    crate::hook::log(&format!("ocr: process spawned ({})", py.display()));
    Ok(OcrProc {
        child,
        stdin,
        reader: BufReader::new(stdout),
    })
}

impl OcrProc {
    fn alive(&mut self) -> bool {
        matches!(self.child.try_wait(), Ok(None))
    }
}

/// 对一张图执行本地 OCR，返回（布局文本, 行, 耗时ms, 设备）。子进程死亡时自动重启。
/// 单次请求 30s 超时保护：读不到响应就杀进程，下次调用重启。
pub fn run(image_path: &str) -> Result<(String, Vec<OcrLine>, u64, String), String> {
    let mut guard = OCR_PROC.lock().map_err(|_| "OCR 进程锁损坏")?;
    let need_spawn = match guard.as_mut() {
        None => true,
        Some(proc) => !proc.alive(),
    };
    if need_spawn {
        *guard = Some(spawn_process()?);
    }
    let proc = guard.as_mut().expect("just spawned");

    let request = serde_json::json!({ "image": image_path }).to_string() + "\n";
    proc.stdin
        .write_all(request.as_bytes())
        .and_then(|_| proc.stdin.flush())
        .map_err(|e| format!("写入 OCR 请求失败：{e}"))?;

    // 带超时的单行读取（1 字节 1 字节凑行，30s 上限；字节缓冲保 UTF-8）
    let deadline = std::time::Instant::now() + Duration::from_secs(30);
    let mut raw: Vec<u8> = Vec::new();
    let mut buf = [0u8; 1];
    loop {
        if std::time::Instant::now() > deadline {
            let _ = proc.child.kill();
            return Err("OCR 超时（30s），进程已重置".into());
        }
        let n = proc
            .reader
            .read(&mut buf)
            .map_err(|e| format!("读取 OCR 响应失败：{e}"))?;
        if n == 0 {
            return Err("OCR 进程无响应（已退出）".into());
        }
        if buf[0] == b'\n' {
            break;
        }
        raw.push(buf[0]);
    }
    let line = String::from_utf8(raw).map_err(|e| format!("OCR 响应非 UTF-8：{e}"))?;
    if line.trim().is_empty() {
        return Err("OCR 进程返回空响应".into());
    }
    let resp: OcrResponse =
        serde_json::from_str(&line).map_err(|e| format!("OCR 响应解析失败：{e}"))?;
    if !resp.ok {
        return Err(format!("OCR 失败：{}", resp.error));
    }
    let text = if resp.layout.is_empty() {
        resp.lines
            .iter()
            .map(|l| l.text.as_str())
            .collect::<Vec<_>>()
            .join("\n")
    } else {
        resp.layout
    };
    Ok((text, resp.lines, resp.elapsed_ms, resp.device))
}

//! glm-5.3-flash 视觉模型调用：配置解析 + OpenAI 兼容 chat/completions。

use std::time::Duration;

pub struct VisionConfig {
    pub base_url: String,
    pub api_key: String,
    pub model: String,
}

fn read_json_file(path: &std::path::Path) -> Option<serde_json::Value> {
    std::fs::read(path).ok().and_then(|b| serde_json::from_slice(&b).ok())
}

/// 配置来源（优先级从高到低）：
/// 1. exe 同目录 snap_vision.json：{"baseUrl": "...", "apiKey": "...", "model": "..."} 任意覆盖
/// 2. %USERPROFILE%\.pi\agent\models.json：providers."zai-coding-cn".baseUrl（模型默认 glm-5.3-flash）
/// 3. %USERPROFILE%\.pi\agent\auth.json："zai-coding-cn".key
pub fn load_config() -> Result<VisionConfig, String> {
    let mut base_url: Option<String> = None;
    let mut api_key: Option<String> = None;
    let mut model: Option<String> = None;

    if let Ok(exe) = std::env::current_exe() {
        if let Some(dir) = exe.parent() {
            if let Some(v) = read_json_file(&dir.join("snap_vision.json")) {
                base_url = v.get("baseUrl").and_then(|x| x.as_str()).map(String::from);
                api_key = v.get("apiKey").and_then(|x| x.as_str()).map(String::from);
                model = v.get("model").and_then(|x| x.as_str()).map(String::from);
            }
        }
    }

    let profile = std::env::var("USERPROFILE")
        .or_else(|_| std::env::var("HOME"))
        .map(std::path::PathBuf::from)
        .unwrap_or_default();

    if base_url.is_none() {
        if let Some(v) = read_json_file(&profile.join(".pi").join("agent").join("models.json")) {
            base_url = v
                .pointer("/providers/zai-coding-cn/baseUrl")
                .and_then(|x| x.as_str())
                .map(String::from);
        }
    }
    if api_key.is_none() {
        if let Some(v) = read_json_file(&profile.join(".pi").join("agent").join("auth.json")) {
            api_key = v
                .pointer("/zai-coding-cn/key")
                .and_then(|x| x.as_str())
                .map(String::from);
        }
    }

    match (base_url, api_key) {
        (Some(base_url), Some(api_key)) => {
            let cfg = VisionConfig {
                base_url: base_url.trim_end_matches('/').to_string(),
                api_key,
                model: model.unwrap_or_else(|| "glm-5.3-flash".into()),
            };
            crate::hook::log(&format!(
                "config loaded: {} model={} key_len={}",
                cfg.base_url,
                cfg.model,
                cfg.api_key.len()
            ));
            Ok(cfg)
        }
        (None, _) => Err("未找到 baseUrl：请在 exe 同目录放 snap_vision.json，或安装 pi 配置（~/.pi/agent/models.json）".into()),
        (_, None) => Err("未找到 apiKey：请在 exe 同目录 snap_vision.json 里配置 apiKey，或登录 pi（~/.pi/agent/auth.json 的 zai-coding-cn.key）".into()),
    }
}

pub struct ChatTurn {
    pub role: String,
    pub text: String,
    /// data:image/...;base64,... 仅 user 消息携带
    pub image_data_url: Option<String>,
}

/// 视觉系统提示词，与 pi 的 glm-vision 扩展保持一致。
fn system_prompt() -> String {
    [
        "你是视觉分析助手，负责观察图片并按用户给出的任务作答。",
        "要求：",
        "1. 只依据图中可见内容回答，不要臆测图中没有的信息；不确定时明确说明。",
        "2. 提取文字（OCR）时逐字保留原文，保持原有结构和换行。",
        "3. 读取图表时给出具体数值、单位和趋势，而不是泛泛描述。",
        "4. 默认用中文回答；任务要求其他语言时除外。",
        "5. 如果任务是提炼搜索关键词，输出一组可直接用于网络搜索的精准查询词。",
    ]
    .join("\n")
}

fn build_body(cfg: &VisionConfig, turns: &[ChatTurn]) -> serde_json::Value {
    let mut messages = vec![serde_json::json!({
        "role": "system",
        "content": system_prompt(),
    })];
    for turn in turns {
        let role = if turn.role == "assistant" { "assistant" } else { "user" };
        let message = match &turn.image_data_url {
            Some(data_url) => serde_json::json!({
                "role": role,
                "content": [
                    { "type": "image_url", "image_url": { "url": data_url } },
                    { "type": "text", "text": turn.text },
                ],
            }),
            None => serde_json::json!({ "role": role, "content": turn.text }),
        };
        messages.push(message);
    }
    serde_json::json!({
        "model": cfg.model,
        "messages": messages,
        "max_tokens": 8192,
    })
}

pub fn chat(turns: &[ChatTurn]) -> Result<String, String> {
    let cfg = match load_config() {
        Ok(cfg) => cfg,
        Err(err) => {
            crate::hook::log(&format!("vision chat aborted: config error: {err}"));
            return Err(err);
        }
    };
    let url = format!("{}/chat/completions", cfg.base_url);
    let body = build_body(&cfg, turns);
    crate::hook::log("vision chat: sending request");

    let agent = ureq::AgentBuilder::new()
        .timeout_connect(Duration::from_secs(15))
        .timeout(Duration::from_secs(180))
        .build();

    let response = agent
        .post(&url)
        .set("Authorization", &format!("Bearer {}", cfg.api_key))
        .send_json(body)
        .map_err(|err| match err {
            ureq::Error::Status(code, resp) => {
                let detail = resp.into_string().unwrap_or_default();
                format!("API 返回 {code}：{}", truncate(&detail, 500))
            }
            other => format!("请求失败：{other}"),
        })?;

    let value: serde_json::Value = response
        .into_json()
        .map_err(|e| format!("解析响应失败：{e}"))?;
    crate::hook::log("vision chat: response parsed");

    let content = value
        .pointer("/choices/0/message/content")
        .and_then(|v| v.as_str())
        .map(|s| s.trim().to_string())
        .filter(|s| !s.is_empty());
    if let Some(content) = content {
        return Ok(content);
    }
    // 个别网关把正文放在 reasoning_content
    if let Some(reasoning) = value
        .pointer("/choices/0/message/reasoning_content")
        .and_then(|v| v.as_str())
    {
        return Ok(reasoning.trim().to_string());
    }
    Err(format!("响应中没有正文：{}", truncate(&value.to_string(), 500)))
}

fn truncate(s: &str, max: usize) -> String {
    if s.chars().count() <= max {
        s.to_string()
    } else {
        let cut: String = s.chars().take(max).collect();
        format!("{cut}…")
    }
}

// glm-5.3-flash 视觉模型调用：配置解析 + OpenAI 兼容 chat/completions。
// 与 Tauri/Dart 版逻辑一致（系统提示词/多轮图片/超时/错误文案）。

using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SnapVision.Services;

public sealed class ChatTurn
{
    public ChatTurn(string role, string text, string imageDataUrl = null)
    {
        Role = role;
        Text = text;
        ImageDataUrl = imageDataUrl;
    }

    public string Role { get; }
    public string Text { get; }

    /// <summary>data:image/...;base64,... 仅 user 消息携带。</summary>
    public string ImageDataUrl { get; }
}

public static class VisionClient
{
    /// <summary>视觉系统提示词，与 pi 的 glm-vision 扩展保持一致。</summary>
    private static readonly string SystemPrompt = string.Join("\n", new[]
    {
        "你是视觉分析助手，负责观察图片并按用户给出的任务作答。",
        "要求：",
        "1. 只依据图中可见内容回答，不要臆测图中没有的信息；不确定时明确说明。",
        "2. 提取文字（OCR）时逐字保留原文，保持原有结构和换行。",
        "3. 读取图表时给出具体数值、单位和趋势，而不是泛泛描述。",
        "4. 默认用中文回答；任务要求其他语言时除外。",
        "5. 如果任务是提炼搜索关键词，输出一组可直接用于网络搜索的精准查询词。",
    });

    /// <summary>共享连接池（DbMcpService 复用）。连接 15s / 请求 180s。</summary>
    private static readonly HttpClient Shared = new(new SocketsHttpHandler
    {
        ConnectTimeout = TimeSpan.FromSeconds(15),
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
    })
    {
        Timeout = TimeSpan.FromSeconds(180),
    };

    public static HttpClient HttpClient => Shared;

    private static JsonObject BuildBody(VisionConfig cfg, List<ChatTurn> turns)
    {
        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "system", ["content"] = SystemPrompt },
        };
        foreach (var turn in turns)
        {
            var role = turn.Role == "assistant" ? "assistant" : "user";
            if (turn.ImageDataUrl != null)
            {
                messages.Add(new JsonObject
                {
                    ["role"] = role,
                    ["content"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["type"] = "image_url",
                            ["image_url"] = new JsonObject { ["url"] = turn.ImageDataUrl },
                        },
                        new JsonObject { ["type"] = "text", ["text"] = turn.Text },
                    },
                });
            }
            else
            {
                messages.Add(new JsonObject { ["role"] = role, ["content"] = turn.Text });
            }
        }
        return new JsonObject
        {
            ["model"] = cfg.Model,
            ["messages"] = messages,
            ["max_tokens"] = 8192,
        };
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s.Substring(0, max) + "…";

    /// <summary>POST chat/completions，返回解析后的 JsonObject；非 200 抛错。</summary>
    public static async Task<JsonObject> HttpChatAsync(VisionConfig cfg, object body)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{cfg.BaseUrl}/chat/completions");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cfg.ApiKey);
        var json = body is JsonNode n ? n.ToJsonString() : JsonSerializer.Serialize(body);
        req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        using var resp = await Shared.SendAsync(req).ConfigureAwait(false);
        var text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            throw new Exception($"API 返回 {(int)resp.StatusCode}：{Truncate(text, 500)}");
        }
        try
        {
            return JsonNode.Parse(text) as JsonObject
                   ?? throw new Exception("解析响应失败：非 JSON 对象");
        }
        catch (Exception e)
        {
            throw new Exception($"解析响应失败：{e.Message}");
        }
    }

    /// <summary>多轮对话 → 正文文本。</summary>
    public static async Task<string> VisionChatAsync(List<ChatTurn> turns)
    {
        VisionConfig cfg;
        try
        {
            cfg = Config.LoadVision();
        }
        catch (Exception err)
        {
            AppLog.Info($"vision chat aborted: config error: {err.Message}");
            throw;
        }
        AppLog.Info("vision chat: sending request");
        try
        {
            var value = await HttpChatAsync(cfg, BuildBody(cfg, turns)).ConfigureAwait(false);
            AppLog.Info("vision chat: response parsed");
            var msg = MessageOf(value);
            var content = TryString(msg?["content"]);
            if (!string.IsNullOrWhiteSpace(content)) return content.Trim();
            // 个别网关把正文放在 reasoning_content
            var reasoning = TryString(msg?["reasoning_content"]);
            if (!string.IsNullOrWhiteSpace(reasoning)) return reasoning.Trim();
            throw new Exception($"响应中没有正文：{Truncate(value.ToJsonString(), 500)}");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            throw new Exception($"请求失败：{e.Message}");
        }
    }

    public static JsonObject MessageOf(JsonObject value)
    {
        try
        {
            var choices = value["choices"] as JsonArray;
            var first = choices?[0] as JsonObject;
            return first?["message"] as JsonObject;
        }
        catch
        {
            return null;
        }
    }

    private static string TryString(JsonNode node)
    {
        try { return node?.GetValue<string>(); }
        catch { return null; }
    }
}

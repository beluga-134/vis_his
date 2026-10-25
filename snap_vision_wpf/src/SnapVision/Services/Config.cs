// 配置解析（与 Tauri/Dart 版一致）：
// 1. exe 同目录 snap_vision.json：{"baseUrl","apiKey","model","dbPath","dbBaseUrl","dbApiKey"} 任意覆盖
// 2. %USERPROFILE%\.pi\agent\models.json → providers."zai-coding-cn".baseUrl（模型默认 glm-5.3-flash）
// 3. %USERPROFILE%\.pi\agent\auth.json → "zai-coding-cn".key

using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SnapVision.Services;

public sealed class VisionConfig
{
    public VisionConfig(string baseUrl, string apiKey, string model)
    {
        BaseUrl = baseUrl;
        ApiKey = apiKey;
        Model = model;
    }

    public string BaseUrl { get; }
    public string ApiKey { get; }
    public string Model { get; }
}

public static class Config
{
    private static JsonObject ReadJson(string path)
    {
        try
        {
            var node = JsonNode.Parse(File.ReadAllText(path));
            return node as JsonObject;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>exe 同目录 snap_vision.json 的指定字段（非空字符串）。</summary>
    public static string Field(string key)
    {
        var v = ReadJson(AppPaths.ConfigPath);
        var field = v?[key];
        var s = field?.GetValue<string>();
        return string.IsNullOrEmpty(s) ? null : s;
    }

    private static string Profile =>
        Environment.GetEnvironmentVariable("USERPROFILE")
        ?? Environment.GetEnvironmentVariable("HOME") ?? "";

    /// <summary>配置加载；找不到时抛出与 Rust/Dart 版同文案的错误。</summary>
    public static VisionConfig LoadVision()
    {
        string baseUrl = null, apiKey = null, model = null;

        var local = ReadJson(AppPaths.ConfigPath);
        if (local != null)
        {
            baseUrl = NonEmpty(local["baseUrl"]);
            apiKey = NonEmpty(local["apiKey"]);
            model = NonEmpty(local["model"]);
        }

        var profile = Profile;
        if (baseUrl == null && profile.Length > 0)
        {
            var v = ReadJson(Path.Combine(profile, ".pi", "agent", "models.json"));
            baseUrl = NonEmpty(v?["providers"]?["zai-coding-cn"]?["baseUrl"]);
        }
        if (apiKey == null && profile.Length > 0)
        {
            var v = ReadJson(Path.Combine(profile, ".pi", "agent", "auth.json"));
            apiKey = NonEmpty(v?["zai-coding-cn"]?["key"]);
        }

        if (baseUrl == null)
            throw new Exception(
                "未找到 baseUrl：请在 exe 同目录放 snap_vision.json，或安装 pi 配置（~/.pi/agent/models.json）");
        if (apiKey == null)
            throw new Exception(
                "未找到 apiKey：请在 exe 同目录 snap_vision.json 里配置 apiKey，或登录 pi（~/.pi/agent/auth.json 的 zai-coding-cn.key）");

        var cfg = new VisionConfig(baseUrl.TrimEnd('/'), apiKey, model ?? "glm-5.3-flash");
        AppLog.Info($"config loaded: {cfg.BaseUrl} model={cfg.Model} key_len={cfg.ApiKey.Length}");
        return cfg;
    }

    private static string NonEmpty(JsonNode node)
    {
        try
        {
            var s = node?.GetValue<string>();
            return string.IsNullOrEmpty(s) ? null : s;
        }
        catch
        {
            return null;
        }
    }
}

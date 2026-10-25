// 界面状态持久化：exe 同目录 snap_vision.ui.json。
// 内容：settings（透明度/引擎/问答模型/玻璃底色/气泡颜色）+ dbList/dbActive。

using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SnapVision.Models;

namespace SnapVision.Services;

/// <summary>默认挂载列表：复杂测试库（5 表 30 列）置首，简单库第二（与 Tauri v2 一致）。</summary>
public static class TestDbs
{
    public const string Complex = "E:/qq_codex/vis_his/snap_vision/selftest/db_mcp/test_db_complex.duckdb";
    public const string Simple = "E:/qq_codex/vis_his/snap_vision/selftest/db_mcp/test_db.duckdb";
}

public sealed class UiState
{
    public Settings Settings { get; set; }
    public List<string> DbList { get; set; }
    public string DbActive { get; set; }
}

public sealed class UiStore
{
    private static readonly Regex Hex6 = new("^#[0-9a-fA-F]{6}$", RegexOptions.Compiled);
    private UiState _cached;

    public UiState Load()
    {
        if (_cached != null) return _cached;
        try
        {
            var v = JsonNode.Parse(File.ReadAllText(AppPaths.UiStorePath)) as JsonObject;
            if (v != null)
            {
                var settings = ParseSettings(v["settings"] as JsonObject);
                var dbList = new List<string>();
                if (v["dbList"] is JsonArray arr)
                {
                    foreach (var item in arr)
                    {
                        try
                        {
                            var s = item?.GetValue<string>();
                            if (!string.IsNullOrWhiteSpace(s)) dbList.Add(s);
                        }
                        catch { }
                    }
                }
                if (dbList.Count == 0) dbList.AddRange(new[] { TestDbs.Complex, TestDbs.Simple });
                var dbActive = TryString(v["dbActive"]);
                return _cached = new UiState
                {
                    Settings = settings,
                    DbList = dbList,
                    DbActive = dbActive,
                };
            }
        }
        catch (Exception e)
        {
            AppLog.Info($"ui_store: load failed, use defaults: {e.Message}");
        }
        return _cached = new UiState
        {
            Settings = Settings.Defaults(),
            DbList = new List<string> { TestDbs.Complex, TestDbs.Simple },
        };
    }

    public void Save(UiState state)
    {
        _cached = state;
        try
        {
            var v = new JsonObject
            {
                ["settings"] = new JsonObject
                {
                    ["opacity"] = state.Settings.Opacity,
                    ["engine"] = state.Settings.Engine.Wire(),
                    ["dbModel"] = state.Settings.DbModel.Wire(),
                    ["glassBg"] = state.Settings.GlassBg,
                    ["userBubble"] = state.Settings.UserBubble,
                    ["assistantBubble"] = state.Settings.AssistantBubble,
                },
                ["dbList"] = new JsonArray(
                    state.DbList.Select(d => JsonValue.Create(d)).ToArray()),
                ["dbActive"] = state.DbActive,
            };
            var json = v.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(AppPaths.UiStorePath, json);
        }
        catch (Exception e)
        {
            AppLog.Info($"ui_store: save failed: {e.Message}");
        }
    }

    private static string TryString(JsonNode node)
    {
        try { return node?.GetValue<string>(); }
        catch { return null; }
    }

    private static Settings ParseSettings(JsonObject v)
    {
        var d = Settings.Defaults();
        if (v == null) return d;
        try
        {
            var opacity = v["opacity"];
            if (opacity != null)
            {
                var num = opacity.GetValue<double>();
                d.Opacity = Math.Clamp(num, 0, 1);
            }
        }
        catch { }
        var engine = TryString(v["engine"]);
        if (engine != null) d.Engine = EngineWire.FromWire(engine);
        var dbModel = TryString(v["dbModel"]);
        if (dbModel != null) d.DbModel = DbModelWire.FromWire(dbModel);
        var glassBg = TryString(v["glassBg"]);
        if (IsHex(glassBg)) d.GlassBg = glassBg;
        var userBubble = TryString(v["userBubble"]);
        if (IsHex(userBubble)) d.UserBubble = userBubble;
        var assistantBubble = TryString(v["assistantBubble"]);
        if (IsHex(assistantBubble)) d.AssistantBubble = assistantBubble;
        return d;
    }

    private static bool IsHex(string v) => v != null && Hex6.IsMatch(v);
}

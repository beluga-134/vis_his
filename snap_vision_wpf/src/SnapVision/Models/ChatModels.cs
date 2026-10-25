// 数据模型：聊天消息、引擎选择、界面设置。
// 与 Dart 版（models/chat_models.dart）字段与默认值逐一对应。

namespace SnapVision.Models;

public enum Engine
{
    Auto,
    Local,
    Glm,
    Db,
}

public enum DbModel
{
    DeepSeekFlash,
    Glm53Flash,
}

public static class EngineWire
{
    public static string Wire(this Engine e) => e switch
    {
        Engine.Auto => "auto",
        Engine.Local => "local",
        Engine.Glm => "glm",
        Engine.Db => "db",
        _ => "auto",
    };

    public static Engine FromWire(string v) => v switch
    {
        "local" => Engine.Local,
        "glm" => Engine.Glm,
        "db" => Engine.Db,
        _ => Engine.Auto,
    };

    public static string Label(this Engine e) => e switch
    {
        Engine.Auto => "自动（截图本地，追问 GLM）",
        Engine.Local => "仅本地 RapidOCR（快）",
        Engine.Glm => "仅 GLM 视觉（深度）",
        Engine.Db => "数据库问答（OCR→查库→答）",
        _ => "auto",
    };
}

public static class DbModelWire
{
    public static string Wire(this DbModel m) =>
        m == DbModel.Glm53Flash ? "glm-5.3-flash" : "deepseek-flash";

    public static DbModel FromWire(string v) =>
        v == "glm-5.3-flash" ? DbModel.Glm53Flash : DbModel.DeepSeekFlash;

    public static string Label(this DbModel m) => m switch
    {
        DbModel.DeepSeekFlash => "DeepSeek-Flash（快，约3s）",
        DbModel.Glm53Flash => "GLM-5.3-Flash（稳，约15s）",
        _ => "",
    };
}

/// <summary>玻璃底色浓度 0–1。</summary>
public sealed class Settings
{
    public double Opacity { get; set; }
    public Engine Engine { get; set; }
    public DbModel DbModel { get; set; }

    /// <summary>窗口玻璃底色 sRGB hex（如 #181c24）。</summary>
    public string GlassBg { get; set; }

    /// <summary>sRGB hex，如 #4d84fe。</summary>
    public string UserBubble { get; set; }
    public string AssistantBubble { get; set; }

    public const double DefaultOpacity = 0.6;

    public static Settings Defaults() => new()
    {
        Opacity = DefaultOpacity,
        Engine = Engine.Auto,
        DbModel = DbModel.DeepSeekFlash,
        GlassBg = "#181c24",
        UserBubble = "#4d84fe",
        AssistantBubble = "#e8eef8",
    };
}

/// <summary>
/// 聊天消息。"user" | "assistant"；ImagePath 仅 user 携带；
/// DbText 是 DB 模式下截图轮 OCR 后回填的识别文本（多轮历史复用）。
/// </summary>
public sealed class ChatMsg
{
    public string Role { get; }
    public string Text { get; }

    /// <summary>截图绝对路径（仅 user 消息携带）。</summary>
    public string ImagePath { get; }

    /// <summary>DB 模式：截图轮本地 OCR 后回填的识别文本。</summary>
    public string DbText { get; set; }

    public ChatMsg(string role, string text, string imagePath = null, string dbText = null)
    {
        Role = role;
        Text = text;
        ImagePath = imagePath;
        DbText = dbText;
    }
}

/// <summary>新截图默认提问任务（与 Tauri/Dart 版一致）。</summary>
public static class ChatConsts
{
    public const string DefaultTask =
        "识别这张截图：如果是表格，按行列结构逐字提取全部内容（保留原始行列对齐）；"
        + "如果是图表或曲线，读出关键数值、单位和趋势；如果是纯文字，逐字提取；"
        + "最后用中文一句话说明这张图是什么。不要臆测图中没有的信息。";

    public const string SysTipText = "按住 Alt 并点击 右键 开始截图，框选后自动交给识别引擎分析。";
}

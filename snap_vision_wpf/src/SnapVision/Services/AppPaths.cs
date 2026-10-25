// 路径约定：配置/日志落在 exe 同目录（与 Tauri/Dart 版一致，便携可迁移），
// 历史产物收进 his/ 子目录（2026-10-25 用户要求：图片和会话记录不再散在根部）。
// - his/his_vis-<UTC时间戳>.jsonl：会话（pi 兼容）
// - his/his_vis-cap-<ms>.png：截图（保留 3 天）
// - his_vis.log：运行日志（>1MB 轮转 .old）
// - snap_vision.json：API/DB 配置覆盖
// - snap_vision.ui.json：界面设置 + 数据库挂载列表

using System.IO;

namespace SnapVision.Services;

public static class AppPaths
{
    public static readonly string Sep = Path.DirectorySeparatorChar.ToString();

    /// <summary>exe 所在目录（便携根）。</summary>
    public static string ExeDir { get; } =
        Path.GetDirectoryName(Environment.ProcessPath ?? AppContext.BaseDirectory);

    public static string LogPath => Path.Combine(ExeDir, "his_vis.log");

    /// <summary>历史产物目录（会话 jsonl + 截图 png），exe 同级 his/。</summary>
    public static string HisDir => Path.Combine(ExeDir, "his");

    public static string CapturePath(long tsMillis)
    {
        try { Directory.CreateDirectory(HisDir); } catch { }
        return Path.Combine(HisDir, $"his_vis-cap-{tsMillis}.png");
    }

    /// <summary>
    /// 一次性迁移：把旧版散落在 exe 目录的历史会话/截图收进 his/（含旧版硬链接
    /// 场景：目标已存在同名硬链接时删掉本侧多余链接，不报错）。
    /// 必须在 Storage 首次定位会话文件之前调用。
    /// </summary>
    public static void MigrateLegacyHisFiles()
    {
        try
        {
            Directory.CreateDirectory(HisDir);
            foreach (var pattern in new[] { "his_vis-*.jsonl", "his_vis-cap-*.png" })
            {
                foreach (var f in Directory.EnumerateFiles(ExeDir, pattern))
                {
                    var dest = Path.Combine(HisDir, Path.GetFileName(f));
                    try
                    {
                        if (!File.Exists(dest)) File.Move(f, dest);
                        else if (File.GetLastWriteTime(f) == File.GetLastWriteTime(dest)) File.Delete(f);
                    }
                    catch { /* 单文件失败忽略，下次启动再试 */ }
                }
            }
        }
        catch { /* 迁移失败不影响启动 */ }
    }

    public static string ConfigPath => Path.Combine(ExeDir, "snap_vision.json");

    public static string UiStorePath => Path.Combine(ExeDir, "snap_vision.ui.json");

    public static string DbPromptPath => Path.Combine(ExeDir, "db_prompt.txt");

    /// <summary>
    /// 启动清理（后台执行，失败静默）：
    /// 1) his_vis-cap-*.png 只留最近 3 天；2) 日志超 1MB 轮转 .old；
    /// 3) 清除旧版遗留在 %TEMP%\snap_vision 的截图与日志。
    /// </summary>
    public static Task CleanupStaleFilesAsync() => Task.Run(() =>
    {
        try
        {
            var cutoff = DateTime.Now.AddDays(-3);
            foreach (var f in Directory.EnumerateFiles(HisDir, "his_vis-cap-*.png"))
            {
                try
                {
                    if (File.GetLastWriteTime(f) < cutoff) File.Delete(f);
                }
                catch { /* 单文件失败忽略 */ }
            }
            try
            {
                var fi = new FileInfo(LogPath);
                if (fi.Exists && fi.Length > 1024 * 1024)
                {
                    var old = LogPath + ".old";
                    try { if (File.Exists(old)) File.Delete(old); } catch { }
                    try { File.Move(LogPath, old); } catch { }
                }
            }
            catch { }
            foreach (var env in new[] { "TEMP", "TMP" })
            {
                var t = Environment.GetEnvironmentVariable(env);
                if (string.IsNullOrEmpty(t)) continue;
                var dir = Path.Combine(t, "snap_vision");
                try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
            }
        }
        catch { /* 清理失败不影响启动 */ }
    });
}

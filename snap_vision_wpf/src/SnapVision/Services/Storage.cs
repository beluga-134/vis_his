// 会话持久化（与 Tauri/Dart 版完全一致）：
// exe 同目录 `his_vis-<UTC时间戳>.jsonl`，pi 兼容格式（首行 session 头，
// message 条目带 id/parentId 链），长期保存，启动时续写最新一份。

using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SnapVision.Services;

public static class Storage
{
    private static string _sessionFile;
    private static string _lastEntryId;
    private static int _idSeq = 1;

    private static long UnixMs() => DateTimeOffset.Now.ToUnixTimeMilliseconds();

    private static readonly DateTime EpochUtc = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static string IsoUtc(long ms)
    {
        var dt = EpochUtc.AddMilliseconds(ms);
        var s = dt.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");
        return s;
    }

    /// <summary>his_vis-20261021-125619.jsonl 风格的 UTC 文件戳。</summary>
    private static string FileStamp(long ms)
    {
        var dt = EpochUtc.AddMilliseconds(ms);
        return $"{dt:yyyyMMdd-HHmmss}";
    }

    private static string NewEntryId()
    {
        var seq = _idSeq++;
        var mix = (ulong)(UnixMs() ^ (seq << 32) ^ (Environment.ProcessId << 16));
        return mix.ToString("x8");
    }

    private static string LatestSessionFile()
    {
        string best = null;
        DateTime bestMtime = DateTime.MinValue;
        try
        {
            foreach (var path in Directory.EnumerateFiles(AppPaths.HisDir, "his_vis-*.jsonl"))
            {
                DateTime mtime;
                try { mtime = File.GetLastWriteTime(path); }
                catch { continue; }
                if (best == null || mtime > bestMtime)
                {
                    best = path;
                    bestMtime = mtime;
                }
            }
        }
        catch { }
        return best;
    }

    private static string EnsureSessionFile()
    {
        if (_sessionFile != null) return _sessionFile;
        var existing = LatestSessionFile();
        if (existing != null)
        {
            // 续写已有会话：恢复末条 id 作为 parentId 链的起点
            try
            {
                var lines = File.ReadAllLines(existing);
                for (var i = lines.Length - 1; i >= 0; i--)
                {
                    var line = lines[i];
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        var v = JsonNode.Parse(line);
                        var id = v?["id"];
                        if (id != null)
                        {
                            _lastEntryId = id.GetValue<string>();
                            break;
                        }
                    }
                    catch { /* 跳过损坏行 */ }
                }
            }
            catch { }
            _sessionFile = existing;
            return existing;
        }
        var ms = UnixMs();
        try { Directory.CreateDirectory(AppPaths.HisDir); } catch { }
        var path = AppPaths.HisDir + AppPaths.Sep + $"his_vis-{FileStamp(ms)}.jsonl";
        var header = new JsonObject
        {
            ["type"] = "session",
            ["version"] = 3,
            ["id"] = ((ulong)ms).ToString("x16"),
            ["timestamp"] = IsoUtc(ms),
            ["cwd"] = AppPaths.ExeDir,
            ["app"] = "snap-vision",
        };
        try
        {
            File.AppendAllText(path, header.ToJsonString() + "\n", Encoding.UTF8);
        }
        catch (Exception e)
        {
            AppLog.Info($"storage: create session failed: {e.Message}");
        }
        _sessionFile = path;
        return path;
    }

    /// <summary>追加一条对话消息（pi 兼容 message 条目；imagePath/dbText 为本应用扩展字段）。</summary>
    public static void AppendMessage(string role, string text,
        string imagePath = null, string dbText = null)
    {
        try
        {
            var file = EnsureSessionFile();
            var id = NewEntryId();
            var message = new JsonObject { ["role"] = role, ["content"] = text };
            if (imagePath != null) message["imagePath"] = imagePath;
            if (dbText != null) message["dbText"] = dbText;
            var entry = new JsonObject
            {
                ["type"] = "message",
                ["id"] = id,
                ["parentId"] = _lastEntryId,
                ["timestamp"] = IsoUtc(UnixMs()),
                ["message"] = message,
            };
            File.AppendAllText(file, entry.ToJsonString() + "\n", Encoding.UTF8);
            _lastEntryId = id;
        }
        catch { /* 持久化失败不阻断聊天 */ }
    }

    /// <summary>
    /// 轮换会话：关闭当前 jsonl（磁盘保留），下次追加时新开一份文件。
    /// 刷子按钮"清空当前对话"用：旧记录不丢，新对话干净开始。
    /// </summary>
    public static void RotateSession()
    {
        _sessionFile = null;
        _lastEntryId = null;
    }

    /// <summary>读取最新会话的全部行（UI 过滤 message 条目恢复界面）。</summary>
    public static List<string> LoadLastLines()
    {
        var latest = LatestSessionFile();
        if (latest == null) return null;
        try
        {
            var lines = File.ReadAllLines(latest)
                .Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
            return lines.Count == 0 ? null : lines;
        }
        catch
        {
            return null;
        }
    }
}

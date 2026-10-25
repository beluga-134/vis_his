// 简单追加日志：exe 同目录 his_vis.log（与 Tauri/Dart 版等价）。
// 毫秒时间戳前缀；串行写队列避免并发追加交错；同步版专供错误回调
// （渲染线程卡死时也能落盘）。

using System.IO;
using System.Text;

namespace SnapVision.Services;

public static class AppLog
{
    private static readonly object Gate = new();

    /// <summary>同步写日志：UI 线程卡死/崩溃时也能落盘。</summary>
    public static void Sync(string msg)
    {
        try
        {
            var ts = DateTimeOffset.Now.ToUnixTimeMilliseconds();
            lock (Gate)
            {
                File.AppendAllText(AppPaths.LogPath, $"{ts} {msg}\n", Encoding.UTF8);
            }
        }
        catch { }
    }

    /// <summary>追加一行日志，失败静默。</summary>
    public static void Info(string msg) => Sync(msg);
}

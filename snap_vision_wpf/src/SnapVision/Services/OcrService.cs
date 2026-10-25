// 本地 RapidOCR：常驻 Python 子进程（stdin/stdout 行 JSON）。
// 与 Tauri/Dart 版逻辑一致：运行时定位、自动重启、30s 超时保护。

using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SnapVision.Services;

public sealed class OcrLine
{
    public string Text { get; set; }
    public double Score { get; set; }
}

public sealed class OcrResult
{
    public string Text { get; set; }
    public List<OcrLine> Lines { get; set; }
    public int ElapsedMs { get; set; }
    public string Device { get; set; }
}

public sealed class OcrService
{
    /// <summary>同一时间只允许一个 OCR 请求（与 Rust 版 Mutex 等价）。</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    private Process _proc;
    private StreamReader _stdout;
    private StreamWriter _stdin;

    /// <summary>
    /// OCR 进程定位：开发机源码热迭代优先，打包版兜底（拷走即用）。
    /// ① .venv-ocr + scripts/ocr_server.py（改 .py 保存即生效）
    /// ② sidecars/ocr_server/ocr_server.exe（PyInstaller 自带解释器+模型）
    /// </summary>
    private static List<string> LocateRuntime()
    {
        var dir = new DirectoryInfo(AppPaths.ExeDir);
        for (var i = 0; i < 5; i++)
        {
            var py = Path.Combine(dir.FullName, ".venv-ocr", "Scripts", "python.exe");
            var script = Path.Combine(dir.FullName, "scripts", "ocr_server.py");
            if (File.Exists(py) && File.Exists(script)) return new List<string> { py, script };
            var bundled = Path.Combine(dir.FullName, "sidecars", "ocr_server", "ocr_server.exe");
            if (File.Exists(bundled)) return new List<string> { bundled };
            if (dir.Parent == null) break;
            dir = dir.Parent;
        }
        return null;
    }

    public async Task<OcrResult> Run(string imagePath)
    {
        await _gate.WaitAsync();
        try
        {
            return await Task.Run(() => RunLocked(imagePath));
        }
        finally
        {
            _gate.Release();
        }
    }

    private bool ProcAlive() => _proc != null && !_proc.HasExited;

    private void Kill()
    {
        var p = _proc;
        _proc = null;
        _stdout = null;
        _stdin = null;
        try { if (p != null && !p.HasExited) p.Kill(); } catch { }
        try { p?.Dispose(); } catch { }
    }

    private void Spawn()
    {
        var runtime = LocateRuntime();
        if (runtime == null)
            throw new Exception(
                "未找到 OCR 运行时：需要 .venv-ocr+scripts/ocr_server.py（开发）或 sidecars/ocr_server/ocr_server.exe（便携）");
        var psi = new ProcessStartInfo
        {
            FileName = runtime[0],
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in runtime.Skip(1)) psi.ArgumentList.Add(arg);
        var p = Process.Start(psi) ?? throw new Exception("OCR 进程启动失败");
        _proc = p;
        _stdin = p.StandardInput;
        _stdout = p.StandardOutput;
        AppLog.Info("ocr: process spawned");
    }

    private OcrResult RunLocked(string imagePath)
    {
        AppLog.Info($"ocr_local: {imagePath}");
        if (!ProcAlive())
        {
            Kill();
            Spawn();
        }
        try
        {
            _stdin.WriteLine(JsonSerializer.Serialize(new Dictionary<string, string> { ["image"] = imagePath }));
            _stdin.Flush();
        }
        catch (Exception e)
        {
            Kill();
            throw new Exception($"写入 OCR 请求失败：{e.Message}");
        }

        string line;
        try
        {
            var read = _stdout.ReadLineAsync();
            if (!read.Wait(TimeSpan.FromSeconds(30)))
            {
                Kill();
                throw new Exception("OCR 超时（30s），进程已重置");
            }
            line = read.Result;
        }
        catch
        {
            Kill();
            throw new Exception("OCR 进程无响应（已退出）");
        }
        if (string.IsNullOrWhiteSpace(line))
            throw new Exception("OCR 进程返回空响应");

        JsonObject resp;
        try
        {
            resp = JsonNode.Parse(line) as JsonObject
                   ?? throw new Exception("OCR 响应解析失败");
        }
        catch (Exception e)
        {
            throw new Exception($"OCR 响应解析失败：{e.Message}");
        }
        var ok = resp["ok"];
        if (ok == null || ok.GetValue<bool>() != true)
        {
            var err = TryString(resp["error"]) ?? "";
            throw new Exception($"OCR 失败：{err}");
        }
        var lines = new List<OcrLine>();
        if (resp["lines"] is JsonArray arr)
        {
            foreach (var item in arr)
            {
                if (item is not JsonObject o) continue;
                double score = 0;
                try { score = o["score"]?.GetValue<double>() ?? 0; } catch { }
                lines.Add(new OcrLine
                {
                    Text = TryString(o["text"]) ?? "",
                    Score = score,
                });
            }
        }
        var layout = TryString(resp["layout"]) ?? "";
        var text = layout.Length == 0 ? string.Join("\n", lines.Select(l => l.Text)) : layout;
        int elapsed = 0;
        try { elapsed = resp["elapsed_ms"]?.GetValue<int>() ?? 0; } catch { }
        var device = TryString(resp["device"]) ?? "";
        AppLog.Info($"ocr_local: {lines.Count} lines in {elapsed}ms on {device}");
        return new OcrResult
        {
            Text = text,
            Lines = lines,
            ElapsedMs = elapsed,
            Device = device,
        };
    }

    private static string TryString(JsonNode node)
    {
        try { return node?.GetValue<string>(); }
        catch { return null; }
    }
}

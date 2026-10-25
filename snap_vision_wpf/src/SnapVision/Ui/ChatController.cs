// 聊天控制器：chat.ts / chat_controller.dart 全部交互逻辑的等价移植。
// 引擎调度（auto/local/glm/db）、取消（Esc/turnGen）、会话恢复、
// 数据库挂载面板、设置热应用全部在这里。

using System.IO;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SnapVision.Models;
using SnapVision.Services;

namespace SnapVision.Ui;

public enum StatusKind { Idle, Busy, Done, Error }

public sealed class ChatController
{
    private readonly UiState _ui;
    private readonly Dictionary<string, ImageSource> _thumbs = new();
    private int _turnGen;
    private long? _lastCaptureTs;

    public ChatController(UiState ui, Dispatcher dispatcher)
    {
        _ui = ui;
        Dispatcher = dispatcher;
        Settings = ui.Settings;
        DbList = ui.DbList;
        DbActive = ui.DbActive;
    }

    public Dispatcher Dispatcher { get; }
    public Settings Settings { get; private set; }
    public List<string> DbList { get; private set; }
    public string DbActive { get; private set; }

    public ObservableCollection<ChatMsg> Messages { get; } = new();
    public IReadOnlyDictionary<string, ImageSource> Thumbs => _thumbs;

    public bool Busy { get; private set; }
    public string DbStatus { get; private set; } = "加载中…";

    public StatusKind StatusKind { get; private set; } = StatusKind.Idle;
    public string StatusText { get; private set; } = "就绪";

    public bool SettingsOpen { get; private set; }
    public bool DbPanelOpen { get; private set; }

    /// <summary>诊断开关：禁用 Markdown 渲染（纯文本兜底）。</summary>
    public bool MdDisabled { get; set; }

    /// <summary>诊断开关：用户气泡用普通文本（排除选择交互）。</summary>
    public bool UserPlain { get; set; }

    /// <summary>状态/设置/面板变化通知（UI 重绘）。</summary>
    public event Action Changed;

    /// <summary>窗口毛玻璃参数变化回调（MainWindow 重设 acrylic）。</summary>
    public event Action GlassChanged;

    public OcrService Ocr { get; } = new();

    private void SetStatus(StatusKind kind, string text)
    {
        StatusKind = kind;
        StatusText = text;
        RaiseChanged();
    }

    private void Notify() => RaiseChanged();

    /// <summary>
    /// 服务层（DbChatFlowAsync/Mount 等）在 ConfigureAwait(false) 后会从线程池线程回调
    /// SetStatus/Notify（DB 引擎进度提示即此路径），不编组会抛
    /// “调用线程无法访问此对象，因为另一个线程拥有此对象”（2026-10-24 实测暴露）。
    /// </summary>
    private void RaiseChanged()
    {
        if (Dispatcher.CheckAccess()) Changed?.Invoke();
        else Dispatcher.BeginInvoke(() => Changed?.Invoke());
    }

    public void ToggleSettingsPanel()
    {
        SettingsOpen = !SettingsOpen;
        DbPanelOpen = false;
        Notify();
    }

    public void ToggleDbPanel()
    {
        DbPanelOpen = !DbPanelOpen;
        SettingsOpen = false;
        Notify();
    }

    public void ClosePanels()
    {
        if (SettingsOpen || DbPanelOpen)
        {
            SettingsOpen = false;
            DbPanelOpen = false;
            Notify();
        }
    }

    private void SaveUi() => new UiStore().Save(_ui);

    // ---- 截图入口（原生桥回传裁剪像素） ----

    public async Task OnNativeCaptureAsync(NativeCapture capture)
    {
        try
        {
            var encoded = await ImageUtils.EncodeCaptureAsync(
                capture.PixelsRgba, capture.Width, capture.Height);
            var ts = DateTimeOffset.Now.ToUnixTimeMilliseconds();
            var path = AppPaths.CapturePath(ts);
            await File.WriteAllBytesAsync(path, encoded.Png);
            _thumbs[path] = LoadImage(encoded.ThumbPng);
            _lastCaptureTs = capture.CapturedAtMs > 0 ? capture.CapturedAtMs : null;
            var msg = new ChatMsg("user", ChatConsts.DefaultTask, imagePath: path);
            Messages.Add(msg);
            Storage.AppendMessage("user", msg.Text, imagePath: path);
            Notify();
            await RunTurnAsync();
        }
        catch (Exception err)
        {
            AppLog.Info($"capture: encode/save failed: {err.Message}");
            SetStatus(StatusKind.Error, $"截图处理失败：{err.Message}");
        }
    }

    private static ImageSource LoadImage(byte[] png)
    {
        try
        {
            var bmp = new BitmapImage();
            using var ms = new MemoryStream(png);
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    // ---- 一轮分析（与 runTurn 逐分支对应） ----

    public async Task RunTurnAsync()
    {
        if (Busy || Messages.Count == 0) return;
        var myGen = ++_turnGen;
        var t0 = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        Busy = true;
        Notify();
        var last = Messages[^1];
        var hasAssistant = Messages.Any(m => m.Role == "assistant");
        var isDb = Settings.Engine == Engine.Db;
        var useLocal = last.ImagePath != null && !isDb &&
            (Settings.Engine == Engine.Local ||
             (Settings.Engine == Engine.Auto && !hasAssistant));

        try
        {
            if (useLocal)
            {
                SetStatus(StatusKind.Busy, "本地 OCR 中…（Esc 取消）");
                var r = await Ocr.Run(last.ImagePath!);
                if (myGen != _turnGen)
                {
                    SetStatus(StatusKind.Idle, "已取消");
                    return;
                }
                var text = r.Text.Length == 0 ? "（未识别到文字）" : r.Text;
                var dev = r.Device == "dml" ? "GPU" : "CPU";
                var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
                var totalMs = _lastCaptureTs != null
                    ? Math.Clamp(now - _lastCaptureTs.Value, 1, 1L << 40)
                    : now - t0;
                _lastCaptureTs = null;
                var reply = $"[本地 RapidOCR · {dev} · 推理{r.ElapsedMs}ms · 全链路{totalMs}ms]\n{text}";
                Messages.Add(new ChatMsg("assistant", reply));
                Storage.AppendMessage("assistant", reply);
                SetStatus(StatusKind.Done, $"本地 OCR 完成（推理 {r.ElapsedMs}ms / 全链路 {totalMs}ms）");
            }
            else if (isDb)
            {
                var queryStatus = Settings.DbModel == DbModel.Glm53Flash
                    ? "GLM 查询数据库中…（Esc 取消）"
                    : "DeepSeek 查询数据库中…（Esc 取消）";
                var ocrStatus = "本地 OCR 中…（完成后自动查询数据库，Esc 取消）";
                SetStatus(StatusKind.Busy,
                    last.ImagePath != null && string.IsNullOrEmpty(last.DbText) ? ocrStatus : queryStatus);
                var result = await DbMcpService.Instance.DbChatFlowAsync(
                    Messages.Select((m, i) => new ChatMsg(
                        m.Role, m.Text,
                        // 旧截图沿用已有识别文本；只识别本轮新图，避免反复识别整段历史。
                        imagePath: i == Messages.Count - 1 ? m.ImagePath : null,
                        dbText: m.DbText)).ToList(),
                    Settings.DbModel.Wire(),
                    Ocr,
                    stage =>
                    {
                        if (myGen == _turnGen && Busy)
                        {
                            SetStatus(StatusKind.Busy, stage == "ocr" ? ocrStatus : queryStatus);
                        }
                    });
                if (myGen != _turnGen) return;
                foreach (var ocrText in result.OcrTexts)
                {
                    if (ocrText.Index < Messages.Count)
                    {
                        Messages[ocrText.Index].DbText = ocrText.Text;
                    }
                }
                // 答复已由服务端在查询完成时落盘，UI 只负责渲染。
                Messages.Add(new ChatMsg("assistant", result.Reply));
                SetStatus(StatusKind.Done, "数据库问答完成");
            }
            else
            {
                SetStatus(StatusKind.Busy, "GLM 分析中…（Esc 取消）");
                var turns = new List<ChatTurn>();
                foreach (var m in Messages)
                {
                    string dataUrl = null;
                    if (m.ImagePath != null)
                    {
                        dataUrl = await ImageUtils.FileToDataUrlAsync(m.ImagePath, 1600);
                    }
                    turns.Add(new ChatTurn(m.Role, m.Text, dataUrl));
                }
                var reply = await VisionClient.VisionChatAsync(turns);
                if (myGen != _turnGen)
                {
                    SetStatus(StatusKind.Idle, "已取消");
                    return;
                }
                Messages.Add(new ChatMsg("assistant", reply));
                Storage.AppendMessage("assistant", reply);
                SetStatus(StatusKind.Done, "完成");
            }
        }
        catch (Exception err)
        {
            // 取消/清空后旧任务的失败也不能覆盖新一轮的状态。
            if (myGen != _turnGen) return;
            Messages.Add(new ChatMsg("assistant", $"⚠ {err.Message}"));
            SetStatus(StatusKind.Error, "失败，可重试或重新截图");
        }
        finally
        {
            if (myGen == _turnGen) Busy = false;
            Notify();
        }
    }

    /// <summary>Esc：取消进行中的分析（在途回传作废）。</summary>
    public void CancelAnalysis()
    {
        if (!Busy) return;
        _turnGen++;
        Busy = false;
        SetStatus(StatusKind.Idle, "已取消本次分析");
    }

    public void SendText(string text)
    {
        var t = text.Trim();
        if (t.Length == 0 || Busy) return;
        Messages.Add(new ChatMsg("user", t));
        Storage.AppendMessage("user", t);
        _ = RunTurnAsync();
        Notify();
    }

    /// <summary>刷子：清空当前对话（内存 + 界面 + 轮换会话文件；磁盘旧记录保留）。</summary>
    public void ClearChat()
    {
        _turnGen++;
        Busy = false;
        Messages.Clear();
        _thumbs.Clear();
        Storage.RotateSession();
        SetStatus(StatusKind.Idle, "已清空对话（旧记录保留在 his/ 目录）");
    }

    /// <summary>助手消息的 Markdown 文档缓存（每条消息解析一次）。</summary>
    /// <summary>
    /// 助手消息的 Markdown 文档。
    /// 不缓存：WPF 元素不能同时挂两个父节点（同文本消息共用缓存会抛
    /// "已是另一个元素的逻辑子元素"），每次渲染重新解析（毫秒级）。
    /// </summary>
    public MdDocument MdDocFor(ChatMsg msg) => MdDocument.Parse(msg.Text);

    // ---- 启动时恢复上次会话（exe 同目录最新 his_vis-*.jsonl） ----

    public void RestoreSession(int? limit = null)
    {
        try
        {
            var lines = Storage.LoadLastLines();
            if (lines == null || lines.Count == 0) return;
            var restored = new List<ChatMsg>();
            foreach (var line in lines)
            {
                try
                {
                    var e = System.Text.Json.Nodes.JsonNode.Parse(line) as System.Text.Json.Nodes.JsonObject;
                    if (e == null) continue;
                    if (e["type"]?.GetValue<string>() != "message") continue;
                    var message = e["message"] as System.Text.Json.Nodes.JsonObject;
                    if (message == null) continue;
                    var role = TryString(message["role"]);
                    if (string.IsNullOrEmpty(role)) continue;
                    var content = TryString(message["content"]);
                    if (string.IsNullOrEmpty(content)) continue;
                    restored.Add(new ChatMsg(
                        role == "assistant" ? "assistant" : "user",
                        content,
                        imagePath: TryString(message["imagePath"]),
                        dbText: TryString(message["dbText"])));
                }
                catch { /* 跳过损坏行 */ }
            }
            if (restored.Count == 0) return;
            if (limit != null && restored.Count > limit)
            {
                restored = restored[^limit.Value..];
            }
            foreach (var m in restored) Messages.Add(m);
            SetStatus(StatusKind.Idle, $"已恢复上次对话（{restored.Count} 条）");
        }
        catch { /* 恢复失败静默，不影响新对话 */ }
    }

    private static string TryString(System.Text.Json.Nodes.JsonNode node)
    {
        try { return node?.GetValue<string>(); }
        catch { return null; }
    }

    // ---- 设置面板 ----

    public void SetOpacity(double v)
    {
        Settings.Opacity = Math.Clamp(v, 0, 1);
        SaveUi();
        GlassChanged?.Invoke();
        // 不调 Notify()：透明度只影响底色，无需重建面板/气泡；
        // 重建会销毁正在拖动的滑块（“拖不动”根因，2026-10-25 日志实锤：
        // 拖动期间每格一次 overlay rebuild）。
    }

    public void SetEngine(Engine engine)
    {
        Settings.Engine = engine;
        SaveUi();
        SetStatus(StatusKind.Idle, $"引擎：{engine.Label()}");
    }

    public void SetDbModel(DbModel model)
    {
        Settings.DbModel = model;
        SaveUi();
        SetStatus(StatusKind.Idle, $"问答模型：{model.Label()}");
    }

    public void SetColors(string glassBg = null, string userBubble = null, string assistantBubble = null)
    {
        if (glassBg != null) Settings.GlassBg = glassBg;
        if (userBubble != null) Settings.UserBubble = userBubble;
        if (assistantBubble != null) Settings.AssistantBubble = assistantBubble;
        SaveUi();
        GlassChanged?.Invoke();
        Notify();
    }

    public void ResetColors()
    {
        var d = Settings.Defaults();
        Settings.GlassBg = d.GlassBg;
        Settings.UserBubble = d.UserBubble;
        Settings.AssistantBubble = d.AssistantBubble;
        SaveUi();
        GlassChanged?.Invoke();
        Notify();
    }

    // ---- 数据库挂载（多库列表：添加 / 删除 / 点选切换） ----

    public async Task<bool> ActivateDbAsync(string path, bool quiet = false)
    {
        if (!quiet)
        {
            DbStatus = "挂载中…";
            Notify();
        }
        try
        {
            var summary = await DbMcpService.Instance.Mount(path);
            DbActive = path;
            DbStatus = $"✅ {summary}";
            SaveUi();
            Notify();
            return true;
        }
        catch (Exception err)
        {
            DbStatus = $"⚠ 挂载失败：{err.Message}";
            Notify();
            return false;
        }
    }

    public async Task AddDbAsync(string path)
    {
        var p = path.Trim();
        if (p.Length == 0) return;
        if (DbList.Contains(p))
        {
            await ActivateDbAsync(p);
            return;
        }
        var ok = await ActivateDbAsync(p);
        if (ok)
        {
            DbList = new List<string>(DbList) { p }; // 追加到尾部：测试库保持首位
            SaveUi();
            Notify();
        }
    }

    public async Task RemoveDbAsync(string path)
    {
        DbList = DbList.Where(p => p != path).ToList();
        if (DbActive == path)
        {
            DbActive = null;
            SaveUi();
            Notify();
            if (DbList.Count > 0)
            {
                await ActivateDbAsync(DbList[0]); // 回列表首位
            }
            else
            {
                DbStatus = "列表为空：未挂载（回退自动查找 snap_vision.json 的 dbPath）";
                Notify();
            }
        }
        else
        {
            SaveUi();
            Notify();
        }
    }

    /// <summary>启动：自动挂载上次选中的库（无记录则首位默认=测试库）。</summary>
    public void InitDbAutoMount()
    {
        var initial = DbActive != null && DbList.Contains(DbActive)
            ? DbActive
            : (DbList.Count > 0 ? DbList[0] : null);
        if (initial != null)
        {
            _ = ActivateDbAsync(initial, quiet: true);
        }
    }
}

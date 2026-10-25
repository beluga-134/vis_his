// Snap Vision（WPF 重构版）入口。
//
// 产品逻辑与 Tauri/Flutter 版一致：
// - Alt+右键 → 框选截图 → 自动进入识别（本地 RapidOCR / GLM 视觉 / 数据库问答）
// - 会话/截图/日志/配置全部落在 exe 同目录
// - 聊天窗永远前台（置顶 + 截图完成即前置），不进入后台渲染状态

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using SnapVision.Services;
using SnapVision.Ui;

namespace SnapVision;

public partial class App : Application
{
    // ---- 单实例：新实例杀旧进程树接管（用户要求：重启/换版本不用手动关旧的） ----

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateMutexW(IntPtr attrs, bool initialOwner, string name);

    [DllImport("kernel32.dll")]
    private static extern bool ReleaseMutex(IntPtr mutex);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    private static IntPtr _mutex;

    private static bool EnsureSingleInstance()
    {
        const string name = "SnapVisionWpfSingleInstance";
        for (var attempt = 0; ; attempt++)
        {
            _mutex = CreateMutexW(IntPtr.Zero, true, name);
            if (Marshal.GetLastWin32Error() != 183 /* ERROR_ALREADY_EXISTS */)
            {
                return true; // 首次启动或旧实例已退出：拿到所有权
            }
            // 关键：即使 ALREADY_EXISTS，本次 Create 也返回了有效句柄；
            // 不关掉的话名字被自己进程占住，重试永远 ALREADY_EXISTS（实测踩坑）
            CloseHandle(_mutex);
            _mutex = IntPtr.Zero;
            if (attempt == 0)
            {
                AppLog.Info("single: existing instance -> kill entire tree & take over");
                foreach (var p in Process.GetProcessesByName("snap-vision"))
                {
                    try
                    {
                        if (p.Id != Environment.ProcessId) p.Kill(entireProcessTree: true);
                    }
                    catch { /* 已退出/权限不足：下一轮重试 */ }
                }
            }
            if (attempt >= 40)
            {
                AppLog.Info("single: old instance did not exit in 4s, starting anyway");
                return true;
            }
            Thread.Sleep(100);
        }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (!EnsureSingleInstance())
        {
            Shutdown();
            return;
        }
        var args = e.Args;
        // 全局异常落盘（对应 Flutter 版 runZonedGuarded/FlutterError.onError）
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
            AppLog.Sync($"DOMAIN_CRASH: {ex.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, ex) =>
            AppLog.Sync($"TASK_CRASH: {ex.Exception}");
        AppLog.Info($"main: start (args={string.Join(" ", args)})");
        AppPaths.MigrateLegacyHisFiles(); // 旧版散落文件收进 his/（必须在 Storage 定位会话前）

        var controller = new ChatController(new UiStore().Load(), Dispatcher);
        controller.MdDisabled = args.Contains("--no-md");
        controller.UserPlain = args.Contains("--user-plain");
        var noDbMount = args.Contains("--no-dbmount");
        var bubble1 = args.Contains("--bubble1");
        var noRestore = args.Contains("--no-restore") || bubble1;

        if (bubble1)
        {
            controller.Messages.Add(new Models.ChatMsg("user", "冒烟测试消息：单条纯文本气泡"));
        }
        if (args.Contains("--two-plain"))
        {
            controller.Messages.Add(new Models.ChatMsg("user", "第一条纯文本消息"));
            controller.Messages.Add(new Models.ChatMsg("user", "第二条纯文本消息"));
        }
        // 内容级二分：--pick-msg=K 只保留恢复消息中的第 K 条（0 起）
        int? pickIdx = null;
        foreach (var a in args)
        {
            if (a.StartsWith("--pick-msg=") &&
                int.TryParse(a["--pick-msg=".Length..], out var k)) pickIdx = k;
        }
        // --keep-idx=0,2 形式：只保留指定下标的消息（配对矩阵用）
        var keepIdx = new List<int>();
        foreach (var a in args)
        {
            if (!a.StartsWith("--keep-idx=")) continue;
            foreach (var part in a["--keep-idx=".Length..].Split(','))
            {
                if (int.TryParse(part, out var v)) keepIdx.Add(v);
            }
        }
        if (keepIdx.Count > 0)
        {
            controller.RestoreSession();
            var restored = controller.Messages.ToList();
            controller.Messages.Clear();
            foreach (var i in keepIdx)
            {
                if (i >= 0 && i < restored.Count) controller.Messages.Add(restored[i]);
            }
        }
        if (pickIdx != null)
        {
            controller.RestoreSession();
            if (pickIdx >= 0 && pickIdx < controller.Messages.Count)
            {
                var keep = controller.Messages[pickIdx.Value];
                controller.Messages.Clear();
                controller.Messages.Add(keep);
            }
        }
        if (!noRestore)
        {
            // 默认只恢复最近 3 条：全量渲染 182 条历史要 4.5s（2026-10-25 启动慢主因）；
            // --restore-all 可回退全量（jsonl 全文仍在 his/，未被截断）
            controller.RestoreSession(limit: args.Contains("--restore-all") ? null : 3);
        }
        AppLog.Info("main: controller ready");

        MainWindow window = null;
        try
        {
            window = new MainWindow(controller, noAcrylic: args.Contains("--no-acrylic"));
            window.Show();
        }
        catch (Exception ex)
        {
            AppLog.Sync($"MAIN_WINDOW_CRASH: {ex}\n{ex.StackTrace}");
            throw;
        }

        _ = AppPaths.CleanupStaleFilesAsync();
        if (!noDbMount) controller.InitDbAutoMount();

        AppLog.Info("main: window shown");
    }

    private void OnDispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        AppLog.Sync($"UI_CRASH: {e.Exception}\n{e.Exception.StackTrace}");
        e.Handled = true; // 不静默死：记录后继续跑（下一轮重试机会）
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_mutex != IntPtr.Zero)
        {
            ReleaseMutex(_mutex);
        }
        AppLog.Info($"main: exit ({e.ApplicationExitCode})");
        base.OnExit(e);
    }
}

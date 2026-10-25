// capture.dll P/Invoke 桥：对应 Dart 版 capture_bridge.dart。
// 回调在捕获线程执行：先 Marshal.Copy 转移所有权，再 sv_free_buffer 释放原生副本。

using System.Runtime.InteropServices;

namespace SnapVision.Services;

public sealed class NativeCapture
{
    public NativeCapture(byte[] pixelsRgba, int width, int height, long capturedAtMs)
    {
        PixelsRgba = pixelsRgba;
        Width = width;
        Height = height;
        CapturedAtMs = capturedAtMs;
    }

    public byte[] PixelsRgba { get; }
    public int Width { get; }
    public int Height { get; }

    /// <summary>松开右键的时刻（unix ms），供 UI 计算全链路耗时。</summary>
    public long CapturedAtMs { get; }
}

public static class CaptureNative
{
    // 委托必须被强引用，防 GC 回收后原生侧回调悬空
    private static SvCaptureCb _callback;
    private static GCHandle _ctxHandle;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void SvCaptureCb(IntPtr rgba, int width, int height,
        long capturedAtMs, IntPtr ctx);

    [DllImport("capture.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern bool sv_init_capture(IntPtr chatHwnd, SvCaptureCb cb, IntPtr ctx);

    [DllImport("capture.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern void sv_free_buffer(IntPtr p);

    private sealed class Ctx
    {
        public Action<NativeCapture> OnCapture;
    }

    /// <summary>
    /// 初始化钩子（Alt+右键拖选、Esc 取消）与捕获线程。
    /// chatHwnd：聊天窗（暗化层挖洞 + 完成后前置）。
    /// </summary>
    public static void Init(IntPtr chatHwnd, Action<NativeCapture> onCapture)
    {
        var ctx = new Ctx { OnCapture = onCapture };
        _ctxHandle = GCHandle.Alloc(ctx);
        _callback = (rgba, width, height, capturedAtMs, _) =>
        {
            var managed = new byte[width * height * 4];
            Marshal.Copy(rgba, managed, 0, managed.Length);
            sv_free_buffer(rgba);
            try
            {
                ctx.OnCapture(new NativeCapture(managed, width, height, capturedAtMs));
            }
            catch (Exception e)
            {
                AppLog.Info($"capture: callback error: {e.Message}");
            }
        };
        if (!sv_init_capture(chatHwnd, _callback, IntPtr.Zero))
        {
            throw new Exception("capture.dll sv_init_capture 失败");
        }
        AppLog.Info("=== snap vision wpf starting, capture bridge ready ===");
    }
}

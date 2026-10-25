// 窗口毛玻璃：SetWindowCompositionAttribute（SWCA）Arylic 配方。
// 与 flutter_acrylic 在 Windows 上的实现同源（ACCENT_ENABLE_ACRYLICBLURBEHIND）；
// Win10/11 均走此路径，失败时上层用实心底色兜底（--no-acrylic 同效果）。

using System.Runtime.InteropServices;

namespace SnapVision.Services;

public static class WindowGlass
{
    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public WindowCompositionAttribute Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    private enum WindowCompositionAttribute
    {
        WCA_ACCENT_POLICY = 19,
    }

    private enum AccentState
    {
        ACCENT_ENABLE_GRADIENT = 1,
        ACCENT_ENABLE_TRANSPARENTGRADIENT = 2,
        ACCENT_ENABLE_BLURBEHIND = 3,
        ACCENT_ENABLE_ACRYLICBLURBEHIND = 4,
        ACCENT_ENABLE_HOSTBACKDROP = 5,
        ACCENT_INVALID_STATE = 6,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public AccentState AccentState;
        public int AccentFlags;
        public uint GradientColor;  // 0xAABBGGRR
        public int AnimationId;
    }

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

    /// <summary>
    /// 应用 Acrylic 毛玻璃。color = 0xAARRGGBB（A 为底色浓度，对应设置面板透明度）。
    /// </summary>
    public static bool ApplyAcrylic(IntPtr hwnd, uint color)
    {
        var accent = new AccentPolicy
        {
            AccentState = AccentState.ACCENT_ENABLE_ACRYLICBLURBEHIND,
            // 把 0xAARRGGBB 重排为 SWCA 需要的 0xAABBGGRR
        };
        accent.GradientColor = (uint)((color & 0xFF000000)
                                      | ((color & 0x000000FF) << 16)
                                      | (color & 0x0000FF00)
                                      | ((color >> 16) & 0x000000FF));
        var data = new WindowCompositionAttributeData
        {
            Attribute = WindowCompositionAttribute.WCA_ACCENT_POLICY,
            Data = Marshal.AllocHGlobal(Marshal.SizeOf<AccentPolicy>()),
            SizeOfData = Marshal.SizeOf<AccentPolicy>(),
        };
        try
        {
            Marshal.StructureToPtr(accent, data.Data, false);
            return SetWindowCompositionAttribute(hwnd, ref data) == 0;
        }
        finally
        {
            Marshal.FreeHGlobal(data.Data);
        }
    }
}

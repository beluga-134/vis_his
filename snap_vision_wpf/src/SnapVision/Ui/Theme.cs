// 主题常量：chat.css / theme.dart 的颜色逐一对应。

using System.Globalization;
using System.Windows.Media;

namespace SnapVision.Ui;

public static class AppColors
{
    public static readonly Color Text = FromRgb(0xFF, 0xEE, 0xF8);
    public static readonly Color TitlebarName = FromRgb(0xC9, 0xD6, 0xEA);
    public static readonly Color SysTipText = FromRgb(0xB9, 0xC6, 0xDA);
    public static readonly Color StatusText = FromRgb(0xA9, 0xB7, 0xCC);
    public static readonly Color DotIdle = FromRgb(0x6B, 0x76, 0x86);
    public static readonly Color DotBusy = FromRgb(0xF5, 0xC5, 0x42);
    public static readonly Color DotDone = FromRgb(0x46, 0xC4, 0x6E);
    public static readonly Color DotError = FromRgb(0xE8, 0x3A, 0x3E);
    public static readonly Color BorderLight = Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF);
    public static readonly Color BorderFaint = Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF);
    public static readonly Color LinkBlue = FromRgb(0x7F, 0xB0, 0xFF);
    public static readonly Color AccentBlue = FromRgb(0x4D, 0x84, 0xFE);
    public static readonly Color HintGray = FromRgb(0x7F, 0x8D, 0xA2);
    public static readonly Color NoteGray = FromRgb(0x93, 0xA2, 0xB8);
    public static readonly Color DbItemText = FromRgb(0xDB, 0xE6, 0xF5);

    public static readonly Brush TextBrush = Frozen(Text);
    public static readonly Brush StatusTextBrush = Frozen(StatusText);
    public static readonly Brush LinkBlueBrush = Frozen(LinkBlue);

    private static Color FromRgb(byte r, byte g, byte b) => Color.FromRgb(r, g, b);

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}

public static class ColorUtil
{
    /// <summary>#rrggbb → Color。</summary>
    public static Color HexToColor(string hex) =>
        Color.FromRgb(
            byte.Parse(hex.Substring(1, 2), NumberStyles.HexNumber),
            byte.Parse(hex.Substring(3, 2), NumberStyles.HexNumber),
            byte.Parse(hex.Substring(5, 2), NumberStyles.HexNumber));

    /// <summary>Color → #RRGGBB（大写）。</summary>
    public static string ColorToHex(Color c) =>
        "#" + (c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2"));
}

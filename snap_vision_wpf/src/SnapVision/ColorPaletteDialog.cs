// 轻量色板选择器（对应 Flutter _ColorPickerDialog / web input[type=color] 角色）。

using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SnapVision.Ui;

namespace SnapVision;

public sealed class ColorPaletteDialog : Window
{
    private static readonly string[] Palette =
    {
        "#181C24", "#101620", "#141A24", "#232A36", "#2B3242",
        "#4D84FE", "#6C5CE7", "#0984E3", "#00B894", "#00CEC9",
        "#FDCB6E", "#E17055", "#D63031", "#E84393", "#FD79A8",
        "#E8EEF8", "#DFE9F5", "#C3CFE2", "#9FB3D1", "#7F8DA2",
    };

    private static readonly Regex Hex6 = new("^#[0-9a-fA-F]{6}$", RegexOptions.Compiled);

    private readonly TaskCompletionSource<string> _tcs = new();
    private string _current;

    private ColorPaletteDialog(Window owner, string initialHex)
    {
        _current = initialHex;
        Owner = owner;
        Title = "选择颜色";
        WindowStyle = WindowStyle.ToolWindow;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI");

        var panel = new StackPanel { Margin = new Thickness(16) };

        var hexBox = new TextBox
        {
            Text = initialHex.ToUpperInvariant(),
            Width = 110,
            FontSize = 12,
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Thickness(4, 3, 4, 3),
        };
        var preview = new Border
        {
            Width = 30,
            Height = 22,
            Margin = new Thickness(10, 0, 0, 0),
            CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(ColorUtil.HexToColor(initialHex)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
        };

        var wrap = new WrapPanel();
        foreach (var hex in Palette)
        {
            var h = hex;
            var dot = new Border
            {
                Width = 28,
                Height = 28,
                Margin = new Thickness(4),
                CornerRadius = new CornerRadius(6),
                Background = new SolidColorBrush(ColorUtil.HexToColor(h)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
            };
            void Refresh()
            {
                dot.BorderBrush = new SolidColorBrush(
                    string.Equals(_current, h, StringComparison.OrdinalIgnoreCase)
                        ? AppColors.LinkBlue
                        : Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
                dot.BorderThickness = new Thickness(
                    string.Equals(_current, h, StringComparison.OrdinalIgnoreCase) ? 2 : 1);
            }
            Refresh();
            dot.MouseLeftButtonDown += (_, _) => { _current = h; Refresh(); preview.Background = new SolidColorBrush(ColorUtil.HexToColor(h)); hexBox.Text = h.ToUpperInvariant(); };
            wrap.Children.Add(dot);
        }
        panel.Children.Add(wrap);

        var hexRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        hexBox.TextChanged += (_, _) =>
        {
            if (Hex6.IsMatch(hexBox.Text))
            {
                _current = hexBox.Text;
                preview.Background = new SolidColorBrush(ColorUtil.HexToColor(hexBox.Text));
            }
        };
        hexRow.Children.Add(hexBox);
        hexRow.Children.Add(preview);
        panel.Children.Add(hexRow);

        var btnRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
        };
        var cancel = new Button { Content = "取消", FontSize = 12, Padding = new Thickness(12, 4, 12, 4), MinWidth = 64 };
        cancel.Click += (_, _) => { _tcs.TrySetResult(null); Close(); };
        var ok = new Button
        {
            Content = "确定",
            FontSize = 12,
            Padding = new Thickness(12, 4, 12, 4),
            MinWidth = 64,
            Margin = new Thickness(8, 0, 0, 0),
            Background = new SolidColorBrush(Color.FromArgb(0x59, 0x4D, 0x84, 0xFE)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x73, 0x78, 0xAA, 0xFF)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xEA, 0xF1, 0xFF)),
        };
        ok.Click += (_, _) => { _tcs.TrySetResult(_current); Close(); };
        btnRow.Children.Add(cancel);
        btnRow.Children.Add(ok);
        panel.Children.Add(btnRow);

        Content = panel;
        Closed += (_, _) => _tcs.TrySetResult(null);
    }

    /// <summary>弹出选择器；取消返回 null。</summary>
    public static Task<string> PickAsync(Window owner, string initialHex)
    {
        var dialog = new ColorPaletteDialog(owner, initialHex);
        dialog.Show();
        return dialog._tcs.Task;
    }
}

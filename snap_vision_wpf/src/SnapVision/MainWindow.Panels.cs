// 浮层面板：⚙ 设置（透明度/引擎/问答模型/颜色）与 🗄 数据库挂载。
// 对应 Flutter 版 panels.dart，全部代码构建（随设置热更新）。

using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using SnapVision.Models;
using SnapVision.Services;
using SnapVision.Ui;

namespace SnapVision;

public partial class MainWindow
{
    private Border _overlayPanel;

    private void RebuildOverlayPanels()
    {
        // 面板挂 OverlayHost（XAML 里 RowSpan=4 全高）：挂主 Grid 会落进 Row0（38px），
        // Margin top=44 → 可用高度为负 → 面板被布局成 0 高度，永远不可见（真“按钮无效”根因）。
        var host = OverlayHost;
        if (_overlayPanel != null)
        {
            host.Children.Remove(_overlayPanel);
            _overlayPanel = null;
        }
        if (_c.SettingsOpen) _overlayPanel = BuildPanelShell(BuildSettingsPanel());
        else if (_c.DbPanelOpen) _overlayPanel = BuildPanelShell(BuildDbPanel());
        AppLog.Info($"ui: overlay rebuild -> {(_overlayPanel != null ? (_c.SettingsOpen ? "settings" : "db") : "none")}");
        if (_overlayPanel != null)
        {
            _overlayPanel.HorizontalAlignment = HorizontalAlignment.Right;
            _overlayPanel.VerticalAlignment = VerticalAlignment.Top;
            _overlayPanel.Margin = new Thickness(0, 44, 10, 0);
            host.Children.Add(_overlayPanel);
        }
    }

    private static Border BuildPanelShell(UIElement child) => new()
    {
        Width = 344,
        Padding = new Thickness(16, 14, 16, 14),
        Background = new SolidColorBrush(Color.FromArgb(0xEB, 0x12, 0x16, 0x1E)),
        BorderBrush = new SolidColorBrush(AppColors.BorderLight),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(12),
        Effect = new DropShadowEffect
        {
            BlurRadius = 44,
            Opacity = 0.45,
            Direction = 270,
            ShadowDepth = 14,
        },
        Child = child,
    };

    private static TextBlock Label(string text) => new()
    {
        Text = text,
        FontSize = 13,
        Foreground = new SolidColorBrush(AppColors.DbItemText),
        VerticalAlignment = VerticalAlignment.Center,
    };

    private static TextBlock Note(string text) => new()
    {
        Text = text,
        FontSize = 11.5,
        LineHeight = 11.5 * 1.5,
        LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
        Foreground = new SolidColorBrush(AppColors.NoteGray),
        TextWrapping = TextWrapping.Wrap,
    };

    // ComboBox 项模板：超长文本省略号截断 + 悬停 ToolTip 看全文
    // （闭合框与下拉列表共用，避免文字被硬切到半个字）
    private static DataTemplate ComboTrimTemplate()
    {
        var tb = new FrameworkElementFactory(typeof(TextBlock));
        tb.SetValue(TextBlock.TextProperty, new Binding("Text"));
        tb.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        tb.SetValue(TextBlock.ToolTipProperty, new Binding("Text"));
        tb.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
        return new DataTemplate { VisualTree = tb };
    }

    // ---------- 设置面板 ----------

    private UIElement BuildSettingsPanel()
    {
        var s = _c.Settings;
        var panel = new StackPanel();

        // 透明度（0–1，步长 0.1）
        var value = new TextBlock
        {
            Text = s.Opacity.ToString("0.0"),
            FontSize = 12,
            Width = 28,
            Foreground = new SolidColorBrush(AppColors.StatusText),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var slider = new Slider
        {
            Minimum = 0,
            Maximum = 1,
            SmallChange = 0.1,
            LargeChange = 0.1,
            Value = Math.Round(s.Opacity, 1),
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 140,
            Cursor = Cursors.Hand,
        };
        slider.ValueChanged += (_, e) =>
        {
            var v = Math.Round(e.NewValue, 1); // 拖动中实时按 0.1 步长取整应用
            value.Text = v.ToString("0.0");
            _c.SetOpacity(v);
        };
        // 松手后才把滑块吸附到 0.1 刻度；拖动过程连续无吸附。
        // （不用 IsSnapToTickEnabled：它会让拖动中的拇指一格一格跳变）
        slider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) =>
            slider.Value = Math.Round(slider.Value, 1)));
        // DockPanel 填充式布局：横向 StackPanel 里 Slider 会塌缩成只剩滑块宽度
        var opacityRow = new DockPanel();
        DockPanel.SetDock(value, Dock.Right);
        opacityRow.Children.Add(value);
        opacityRow.Children.Add(slider);
        panel.Children.Add(Row("透明度", opacityRow));

        var engineBox = new ComboBox
        {
            FontSize = 12.5,
            Foreground = new SolidColorBrush(AppColors.Text),
            ItemTemplate = ComboTrimTemplate(),
        };
        foreach (var e in Enum.GetValues<Engine>()) engineBox.Items.Add(new CbItem(e.Label(), e));
        engineBox.SelectedIndex = (int)s.Engine;
        var modelBox = new ComboBox
        {
            FontSize = 12.5,
            Foreground = new SolidColorBrush(AppColors.Text),
            IsEnabled = s.Engine == Engine.Db,
            Opacity = s.Engine == Engine.Db ? 1 : 0.45,
            ItemTemplate = ComboTrimTemplate(),
        };
        foreach (var m in Enum.GetValues<DbModel>()) modelBox.Items.Add(new CbItem(m.Label(), m));
        modelBox.SelectedIndex = (int)s.DbModel;
        // 先设初值再挂事件：避免初始化期触发一次无谓的 Set/保存
        engineBox.SelectionChanged += (_, _) =>
        {
            if (engineBox.SelectedItem is CbItem it) _c.SetEngine((Engine)it.Value);
        };
        engineBox.SelectionChanged += (_, _) =>
        {
            var isDb = _c.Settings.Engine == Engine.Db;
            modelBox.IsEnabled = isDb;
            modelBox.Opacity = isDb ? 1 : 0.45;
        };
        panel.Children.Add(Row("识别引擎", engineBox));

        // 问答模型（仅 db 引擎可用）
        modelBox.SelectionChanged += (_, _) =>
        {
            if (modelBox.SelectedItem is CbItem it) _c.SetDbModel((DbModel)it.Value);
        };
        panel.Children.Add(Row("问答模型", modelBox));

        // 颜色行
        var colorRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 6) };
        colorRow.Children.Add(Label("窗口底色"));
        colorRow.Children.Add(Spacer(8));
        colorRow.Children.Add(ColorDot(s.GlassBg, v => _c.SetColors(glassBg: v)));
        colorRow.Children.Add(Spacer(16));
        colorRow.Children.Add(Label("我的气泡"));
        colorRow.Children.Add(Spacer(8));
        colorRow.Children.Add(ColorDot(s.UserBubble, v => _c.SetColors(userBubble: v)));
        colorRow.Children.Add(Spacer(16));
        colorRow.Children.Add(Label("助手气泡"));
        colorRow.Children.Add(Spacer(8));
        colorRow.Children.Add(ColorDot(s.AssistantBubble, v => _c.SetColors(assistantBubble: v)));
        panel.Children.Add(colorRow);

        var noteRow = new StackPanel { Orientation = Orientation.Horizontal };
        noteRow.Children.Add(Note("sRGB 十六进制"));
        var reset = new Button
        {
            Content = "恢复默认",
            FontSize = 11.5,
            Foreground = new SolidColorBrush(AppColors.LinkBlue),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(4, 0, 4, 0),
            Cursor = Cursors.Hand,
            Margin = new Thickness(0, 6, 0, 0),
        };
        reset.Click += (_, _) => _c.ResetColors();
        var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        right.Children.Add(reset);
        var noteWrap = new DockPanel();
        DockPanel.SetDock(right, Dock.Right);
        noteWrap.Children.Add(right);
        noteWrap.Children.Add(Note(""));
        noteWrap.Margin = new Thickness(0, 6, 0, 0);
        panel.Children.Add(noteWrap);

        panel.Children.Add(new Separator
        {
            Margin = new Thickness(0, 8, 0, 8),
            Background = new SolidColorBrush(AppColors.BorderFaint),
        });
        panel.Children.Add(Note("本地引擎：RapidOCR 常驻进程，约 1 秒；GLM：glm-5.3-flash，表格/图表更准。"));
        return panel;
    }

    private sealed record CbItem(string Text, object Value)
    {
        public override string ToString() => Text;
    }

    private static UIElement Horiz(params UIElement[] children)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var c in children) row.Children.Add(c);
        return row;
    }

    private static FrameworkElement Spacer(double w) => new Border { Width = w };

    private static Grid Row(string label, UIElement control)
    {
        var grid = new Grid { Margin = new Thickness(0, 5, 0, 5) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        var l = Label(label);
        Grid.SetColumn(l, 0);
        Grid.SetColumn(control, 1);
        grid.Children.Add(l);
        grid.Children.Add(control);
        return grid;
    }

    private Border ColorDot(string hex, Action<string> onPick)
    {
        var dot = new Border
        {
            Width = 24,
            Height = 18,
            CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(ColorUtil.HexToColor(hex)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
        };
        dot.MouseLeftButtonDown += async (_, e) =>
        {
            e.Handled = true;
            var picked = await ColorPaletteDialog.PickAsync(this, hex);
            if (picked != null) onPick(picked);
        };
        return dot;
    }

    // ---------- 数据库面板 ----------

    private UIElement BuildDbPanel()
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = "数据库挂载",
            FontSize = 12.5,
            Foreground = new SolidColorBrush(AppColors.TitlebarName),
            Margin = new Thickness(0, 0, 0, 10),
        });

        var pathBox = new TextBox
        {
            FontSize = 12,
            Foreground = new SolidColorBrush(AppColors.Text),
            Background = new SolidColorBrush(Color.FromArgb(0x40, 0, 0, 0)),
            BorderBrush = new SolidColorBrush(AppColors.BorderLight),
            Padding = new Thickness(8, 6, 8, 6),
        };
        var addBtn = new Button
        {
            Content = "添加",
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(0xEA, 0xF1, 0xFF)),
            Background = new SolidColorBrush(Color.FromArgb(0x59, 0x4D, 0x84, 0xFE)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x73, 0x78, 0xAA, 0xFF)),
            Padding = new Thickness(14, 6, 14, 6),
            Cursor = Cursors.Hand,
            Margin = new Thickness(8, 0, 0, 0),
        };
        addBtn.Click += (_, _) => _ = _c.AddDbAsync(pathBox.Text);
        pathBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) _ = _c.AddDbAsync(pathBox.Text);
        };

        var listPanel = new StackPanel { Margin = new Thickness(0, 10, 0, 10) };
        var listHost = new ScrollViewer
        {
            MaxHeight = 150,
            Content = listPanel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        void RebuildList()
        {
            listPanel.Children.Clear();
            foreach (var path in _c.DbList)
            {
                var p = path;
                var active = p == _c.DbActive;
                var item = new Border
                {
                    Margin = new Thickness(0, 0, 0, 4),
                    Padding = new Thickness(8, 5, 8, 5),
                    CornerRadius = new CornerRadius(8),
                    BorderBrush = new SolidColorBrush(active
                        ? Color.FromArgb(0x99, 0x46, 0xC4, 0x6E)
                        : AppColors.BorderFaint),
                    Background = new SolidColorBrush(active
                        ? Color.FromArgb(0x14, 0x46, 0xC4, 0x6E)
                        : Color.FromArgb(0x33, 0, 0, 0)),
                    Cursor = Cursors.Hand,
                };
                var row = new StackPanel { Orientation = Orientation.Horizontal };
                row.Children.Add(new Ellipse
                {
                    Width = 8,
                    Height = 8,
                    Fill = new SolidColorBrush(active
                        ? AppColors.DotDone
                        : Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
                    VerticalAlignment = VerticalAlignment.Center,
                });
                row.Children.Add(Spacer(8));
                var text = new TextBlock
                {
                    Text = p,
                    FontSize = 11.5,
                    Foreground = new SolidColorBrush(AppColors.DbItemText),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    ToolTip = $"{p}（点击切换）",
                    VerticalAlignment = VerticalAlignment.Center,
                };
                var textHost = new Grid();
                textHost.ColumnDefinitions.Add(new ColumnDefinition());
                Grid.SetColumn(text, 0);
                textHost.Children.Add(text);
                row.Children.Add(textHost);
                var del = new TextBlock
                {
                    Text = "✕",
                    FontSize = 11,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x97, 0xAB)),
                    Margin = new Thickness(4, 0, 2, 0),
                    Cursor = Cursors.Hand,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                del.MouseLeftButtonDown += (_, e) =>
                {
                    e.Handled = true;
                    _ = _c.RemoveDbAsync(p);
                };
                row.Children.Add(del);
                item.Child = row;
                item.MouseLeftButtonDown += (_, _) => _ = _c.ActivateDbAsync(p);
                listPanel.Children.Add(item);
            }
        }
        RebuildList();
        var dbStatus = new TextBlock
        {
            Text = _c.DbStatus,
            FontSize = 11.5,
            LineHeight = 11.5 * 1.5,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            Foreground = new SolidColorBrush(Color.FromRgb(0x93, 0xA2, 0xB8)),
            TextWrapping = TextWrapping.Wrap,
        };
        // 状态变化时刷新列表与状态行（面板关闭即退订）
        _c.Changed += OnDbPanelRefresh;
        void OnDbPanelRefresh()
        {
            if (!_c.DbPanelOpen)
            {
                _c.Changed -= OnDbPanelRefresh;
                return;
            }
            RebuildList();
            dbStatus.Text = _c.DbStatus;
        }

        var pathGrid = new Grid();
        pathGrid.ColumnDefinitions.Add(new ColumnDefinition());
        pathGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(pathBox, 0);
        Grid.SetColumn(addBtn, 1);
        pathGrid.Children.Add(pathBox);
        pathGrid.Children.Add(addBtn);
        panel.Children.Add(pathGrid);
        panel.Children.Add(listHost);
        panel.Children.Add(dbStatus);
        panel.Children.Add(Spacer(1));
        panel.Children.Add(Note("点条目切换当前库（绿点）；✕ 从列表删除；首位为默认。DuckDB 只读挂载，切换立即重启 MCP server。"));
        return panel;
    }
}

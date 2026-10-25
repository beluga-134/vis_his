// 主窗口：无边框置顶毛玻璃窗 + 聊天流渲染 + 状态条 + 输入区 + 浮层面板。
// 对应 Flutter 版 main.dart + app_shell.dart + chat_log.dart。

using System.Collections.Specialized;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using System.Windows.Media.Imaging;
using SnapVision.Models;
using SnapVision.Services;
using SnapVision.Ui;

namespace SnapVision;

public partial class MainWindow : Window
{
    private readonly ChatController _c;
    private readonly bool _noAcrylic;
    private IntPtr _hwnd;
    private DoubleAnimation _pulse;
    private Border _sysTip;
    private Border _lightbox;
    private bool _suppressAutoScroll;

    public MainWindow(ChatController controller, bool noAcrylic)
    {
        _c = controller;
        _noAcrylic = noAcrylic;
        InitializeComponent();

        _c.Changed += OnControllerChanged;
        _c.GlassChanged += ApplyGlass;
        _c.Messages.CollectionChanged += OnMessagesChanged;

        RebuildChat();
        UpdateStatus();
        Loaded += (_, _) => ComposerBox.Focus();
        Activated += (_, _) => ComposerBox.Focus();
        StartRenderKeepAlive();
    }

    private static T? FindVisualDescendant<T>(System.Windows.DependencyObject root) where T : System.Windows.DependencyObject
    {
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T typed) return typed;
            var found = FindVisualDescendant<T>(child);
            if (found != null) return found;
        }
        return null;
    }

    /// <summary>
    /// 渲染保活：本窗口是 NOACTIVATE+TOPMOST 浮层，下面常有全屏应用（如 Retro Messenger），
    /// DWM 判定被遮挡时 WPF 会节流渲染——面板/气泡点了不开，隔几秒才补一帧，
    /// 观感即“按钮没生效”。用 1×1 透明元素的无限动画保持渲染时钟持续 tick（时钟小组件同款解法）。
    /// </summary>
    private void StartRenderKeepAlive()
    {
        var dot = new System.Windows.Shapes.Rectangle
        {
            Width = 1,
            Height = 1,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Fill = Brushes.Transparent,
            Opacity = 0.005,
        };
        var root = (Grid)RootBorder.Child;
        root.Children.Add(dot);
        var anim = new DoubleAnimation(0.005, 0.02, TimeSpan.FromMilliseconds(800))
        {
            RepeatBehavior = RepeatBehavior.Forever,
            AutoReverse = true,
        };
        dot.BeginAnimation(UIElement.OpacityProperty, anim);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        TryEnableDwmRoundedCorners();
        ApplyGlass();
        try
        {
            CaptureNative.Init(_hwnd, cap =>
                Dispatcher.BeginInvoke(async () => await _c.OnNativeCaptureAsync(cap)));
        }
        catch (Exception ex)
        {
            AppLog.Info($"capture init failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Win11：让 DWM 的系统边框/阴影也走圆角（DWMWCP_ROUND≈8px，与 RootBorder 一致），
    /// 否则系统会在圆角内容四角画方形边框残留。Win10 无此属性，静默忽略。
    /// </summary>
    private void TryEnableDwmRoundedCorners()
    {
        try
        {
            var pref = 2; // DWMWCP_ROUND
            DwmSetWindowAttribute(_hwnd, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref pref, sizeof(int));
        }
        catch { /* Win10/不支持：分层窗口本就无系统边框 */ }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    private void ApplyGlass()
    {
        if (_hwnd == IntPtr.Zero) return;
        try
        {
            var s = _c.Settings;
            var glass = ColorUtil.HexToColor(s.GlassBg);
            var argb = (uint)((byte)(s.Opacity * 255) << 24 | glass.R << 16 | glass.G << 8 | glass.B);
            if (_noAcrylic || !WindowGlass.ApplyAcrylic(_hwnd, argb))
            {
                // 兜底实底跟随透明度滑杆（0–1 线性映射：0=全透明底、1=不透明），
                // 与亚克力路径的 opacity×255 保持一致；
                // 2026-10-24 曾写死 0xF5 且滑杆不生效，用户反馈“透明度问题”。
                var alpha = (byte)Math.Round(255 * s.Opacity);
                var solid = Color.FromArgb(alpha, glass.R, glass.G, glass.B);
                RootBorder.Background = new SolidColorBrush(solid);
                AppLog.Info($"glass: solid fallback (noAcrylic={_noAcrylic}, opacity={s.Opacity:0.00}, alpha={alpha})");
            }
            else
            {
                RootBorder.Background = Brushes.Transparent;
            }
        }
        catch (Exception ex)
        {
            AppLog.Info($"applyGlass failed: {ex.Message}");
        }
    }

    // ---- 标题栏 / 窗口操作 ----

    private void OnRootMouseDown(object sender, MouseButtonEventArgs e)
    {
        AppLog.Info($"ui: RootMouseDown src={e.OriginalSource?.GetType().Name} at ({e.GetPosition(this).X:0},{e.GetPosition(this).Y:0}) btn={e.LeftButton}");
        if (e.LeftButton != MouseButtonState.Pressed) return;
        // 点击在浮层面板内部（滑块/下拉/色板/输入）不关面板，只有面板外才收起
        if (_overlayPanel != null && e.OriginalSource is DependencyObject src && IsInOverlay(src))
        {
            return;
        }
        _c.ClosePanels();
    }

    private bool IsInOverlay(DependencyObject src)
    {
        for (var cur = src; cur != null;)
        {
            if (ReferenceEquals(cur, _overlayPanel)) return true;
            cur = cur is System.Windows.Media.Visual
                ? System.Windows.Media.VisualTreeHelper.GetParent(cur)
                : LogicalTreeHelper.GetParent(cur);
        }
        return false;
    }

    private void OnTitlebarDown(object sender, MouseButtonEventArgs e)
    {
        AppLog.Info($"ui: TitlebarDown src={e.OriginalSource?.GetType().Name} clickCount={e.ClickCount} btn={e.LeftButton}");
        if (e.ClickCount >= 2)
        {
            Hide(); // 双击隐藏（对应 Flutter onDoubleTap）
            return;
        }
        // CaptionHeight=0 方案：拖拽走 Win32 SC_DRAGMOVE（系统级移动，含贴边提示）。
        // 2026-10-25 用户报“窗口拖不动”：日志坐实 DragMove() 在事件被延迟处理时
        //（如滑块 Thumb 释放捕获后的时序）抛 InvalidOperationException 被吞，
        // 且随后出现 btn=Released、坐标(0,0) 的迟到 MouseDown。SC_DRAGMOVE 按
        // 物理输入状态判断，不依赖 WPF 缓存的按钮状态。
        if (_hwnd != IntPtr.Zero)
        {
            SendMessage(_hwnd, 0x0112 /* WM_SYSCOMMAND */, (IntPtr)0xF012 /* SC_DRAGMOVE */, IntPtr.Zero);
            return;
        }
        try { DragMove(); }
        catch (InvalidOperationException) { /* 松开过快时 DragMove 会抛，忽略 */ }
    }

    private void OnToggleSettings(object sender, RoutedEventArgs e) { AppLog.Info("ui: click settings"); _c.ToggleSettingsPanel(); }

    private void OnToggleDb(object sender, RoutedEventArgs e) { AppLog.Info("ui: click db"); _c.ToggleDbPanel(); }

    private void OnClearChat(object sender, RoutedEventArgs e) { AppLog.Info("ui: click clear"); _c.ClearChat(); }

    private void OnHide(object sender, RoutedEventArgs e) { AppLog.Info("ui: click hide"); Hide(); }

    private void OnClose(object sender, RoutedEventArgs e) { AppLog.Info("ui: click close"); Application.Current.Shutdown(); }

    /// <summary>
    /// 状态变化后强制补一帧：本窗口是 NOACTIVATE+TOPMOST 浮层，下面常有全屏应用，
    /// 无输入时 WPF 渲染会被节流——面板点了不开、按钮高亮几秒后才出现，观感即“按钮没生效”。
    /// 空闲优先级回调 + RedrawWindow(UPDATENOW) 双保险，确保变化立即上屏。
    /// </summary>
    private void ForceRender()
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (_hwnd != IntPtr.Zero)
            {
                RedrawWindow(_hwnd, IntPtr.Zero, IntPtr.Zero,
                    0x0001 /*RDW_INVALIDATE*/ | 0x0080 /*RDW_ALLCHILDREN*/ | 0x0100 /*RDW_UPDATENOW*/);
            }
        });
    }

    [DllImport("user32.dll")]
    private static extern bool RedrawWindow(IntPtr hwnd, IntPtr lprcUpdate, IntPtr hrgnUpdate, uint flags);

    // ---- 聊天流渲染 ----

    private void OnMessagesChanged(object sender, NotifyCollectionChangedEventArgs e)
    {
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add:
                foreach (ChatMsg msg in e.NewItems) AppendBubble(msg);
                break;
            case NotifyCollectionChangedAction.Reset:
            case NotifyCollectionChangedAction.Remove:
                RebuildChat();
                break;
        }
        UpdateSysTip();
        AutoScroll();
        ForceRender();
    }

    private void OnControllerChanged()
    {
        AppLog.Info($"ui: controller changed settings={_c.SettingsOpen} db={_c.DbPanelOpen} msgs={_c.Messages.Count}");
        UpdateStatus();
        RebuildOverlayPanels();
        ForceRender();
        if (_c.SettingsOpen || _c.DbPanelOpen) return;
        // 气泡颜色变化：整体重建
        RebuildChat();
    }

    private void RebuildChat()
    {
        ChatPanel.Children.Clear();
        foreach (var msg in _c.Messages) AppendBubble(msg);
        UpdateSysTip();
        AutoScroll(force: true);
    }

    private void UpdateSysTip()
    {
        var show = _c.Messages.Count == 0;
        if (show && _sysTip == null)
        {
            _sysTip = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)),
                CornerRadius = new CornerRadius(999),
                Padding = new Thickness(14, 8, 14, 8),
                Margin = new Thickness(0, 24, 0, 8),
                HorizontalAlignment = HorizontalAlignment.Center,
                Child = new TextBlock
                {
                    Text = ChatConsts.SysTipText,
                    FontSize = 12,
                    Foreground = new SolidColorBrush(AppColors.SysTipText),
                },
            };
            ChatPanel.Children.Add(_sysTip);
        }
        else if (!show && _sysTip != null)
        {
            ChatPanel.Children.Remove(_sysTip);
            _sysTip = null;
        }
    }

    private void AppendBubble(ChatMsg msg)
    {
        var bubble = BuildBubble(msg);
        ChatPanel.Children.Add(bubble);
    }

    private Border BuildBubble(ChatMsg msg)
    {
        var isUser = msg.Role == "user";
        var s = _c.Settings;
        var bubbleColor = ColorUtil.HexToColor(isUser ? s.UserBubble : s.AssistantBubble);
        var fillAlpha = (byte)Math.Round((isUser ? 0.34 : 0.10) * 255);
        var borderAlpha = (byte)Math.Round((isUser ? 0.5 : 0.2) * 255);

        var fill = Color.FromArgb(fillAlpha, bubbleColor.R, bubbleColor.G, bubbleColor.B);
        var border = Color.FromArgb(borderAlpha, bubbleColor.R, bubbleColor.G, bubbleColor.B);

        var corner = isUser
            ? new CornerRadius(12, 12, 12, 4)
            : new CornerRadius(12, 12, 4, 12);

        var stack = new StackPanel();

        // 缩略图（双击放大）
        if (msg.ImagePath != null && _c.Thumbs.TryGetValue(msg.ImagePath, out var thumb) && thumb != null)
        {
            var img = new Image
            {
                Source = thumb,
                Stretch = Stretch.Uniform,
                MaxHeight = 180,
                StretchDirection = StretchDirection.DownOnly,
                Margin = new Thickness(0, 0, 0, 6),
                Cursor = Cursors.Hand,
            };
            img.MouseLeftButtonDown += (_, e) =>
            {
                if (e.ClickCount >= 2) OpenLightbox(msg.ImagePath);
            };
            var clip = new Border { CornerRadius = new CornerRadius(8), ClipToBounds = true, Child = img };
            stack.Children.Add(clip);
        }

        // 正文
        if (isUser)
        {
            if (_c.UserPlain)
            {
                stack.Children.Add(new TextBlock
                {
                    Text = msg.Text,
                    FontSize = 13.5,
                    LineHeight = 13.5 * 1.55,
                    LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                    Foreground = new SolidColorBrush(AppColors.Text),
                    TextWrapping = TextWrapping.Wrap,
                });
            }
            else
            {
                // 可选中复制的用户文本
                var box = new TextBox
                {
                    Text = msg.Text,
                    IsReadOnly = true,
                    BorderThickness = new Thickness(0),
                    Background = Brushes.Transparent,
                    FontSize = 13.5,
                    Foreground = new SolidColorBrush(AppColors.Text),
                    TextWrapping = TextWrapping.Wrap,
                    Padding = new Thickness(0),
                    Cursor = Cursors.IBeam,
                };
                box.Loaded += (_, _) => box.Select(0, 0);
                stack.Children.Add(box);
            }
        }
        else
        {
            FrameworkElement body;
            if (_c.MdDisabled)
            {
                body = new TextBlock
                {
                    Text = msg.Text,
                    FontSize = 13.5,
                    LineHeight = 13.5 * 1.55,
                    LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                    Foreground = new SolidColorBrush(AppColors.Text),
                    TextWrapping = TextWrapping.Wrap,
                };
            }
            else
            {
                var md = new StackPanel();
                try
                {
                    foreach (var block in _c.MdDocFor(msg).Blocks) md.Children.Add(block);
                }
                catch (Exception ex)
                {
                    AppLog.Info($"md render failed: {ex.Message}");
                    md.Children.Add(new TextBlock
                    {
                        Text = msg.Text,
                        Foreground = new SolidColorBrush(AppColors.Text),
                        TextWrapping = TextWrapping.Wrap,
                    });
                }
                body = md;
            }
            stack.Children.Add(body);
        }

        return new Border
        {
            HorizontalAlignment = isUser ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            MaxWidth = 440,
            Margin = new Thickness(0, 0, 0, 10),
            Padding = new Thickness(12, 9, 12, 9),
            Background = new SolidColorBrush(fill),
            BorderBrush = new SolidColorBrush(border),
            BorderThickness = new Thickness(1),
            CornerRadius = corner,
            Child = stack,
        };
    }

    private void AutoScroll(bool force = false)
    {
        if (!force && _suppressAutoScroll) return;
        Dispatcher.BeginInvoke(() =>
        {
            ChatScroll.ScrollToEnd();
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void OnChatScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // 用户滚离底部（>80px）时暂停自动跟底，回到底部恢复（对应 web/Flutter 版语义）
        var max = ChatScroll.ScrollableHeight;
        _suppressAutoScroll = max > 0 && max - ChatScroll.VerticalOffset > 80;
    }

    private void OnChatWheel(object sender, MouseWheelEventArgs e) { /* 默认滚动即可 */ }

    private void OpenLightbox(string path)
    {
        if (_lightbox != null) return;
        Image img;
        try
        {
            var bmp = new BitmapImage();
            using var fs = File.OpenRead(path);
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = fs;
            bmp.EndInit();
            bmp.Freeze();
            img = new Image { Source = bmp, Stretch = Stretch.Uniform };
        }
        catch
        {
            img = new Image();
        }
        _lightbox = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xC7, 0, 0, 0)),
            Child = new Border
            {
                MaxWidth = 530,
                MaxHeight = 500,
                CornerRadius = new CornerRadius(8),
                ClipToBounds = true,
                Child = img,
            },
        };
        img.Stretch = Stretch.Uniform;
        MouseDown += CloseLightbox;
        _lightbox.MouseDown += (_, _) => CloseLightbox(null, null);
        // 挂到根网格（覆盖全窗）
        var root = (Grid)RootBorder.Child;
        Grid.SetRowSpan(_lightbox, 4);
        root.Children.Add(_lightbox);
    }

    private void CloseLightbox(object sender, MouseButtonEventArgs e)
    {
        if (_lightbox == null) return;
        var root = (Grid)RootBorder.Child;
        root.Children.Remove(_lightbox);
        _lightbox = null;
    }

    // ---- 状态条 ----

    private void UpdateStatus()
    {
        StatusText.Text = _c.StatusText;
        var color = _c.StatusKind switch
        {
            StatusKind.Busy => AppColors.DotBusy,
            StatusKind.Done => AppColors.DotDone,
            StatusKind.Error => AppColors.DotError,
            _ => AppColors.DotIdle,
        };
        StatusDot.Fill = new SolidColorBrush(color);

        var pulsing = _c.StatusKind is StatusKind.Busy or StatusKind.Error;
        if (pulsing && _pulse == null)
        {
            _pulse = new DoubleAnimation(0.35, 1.0, TimeSpan.FromMilliseconds(550))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
            };
            StatusDot.BeginAnimation(UIElement.OpacityProperty, _pulse);
        }
        else if (!pulsing && _pulse != null)
        {
            StatusDot.BeginAnimation(UIElement.OpacityProperty, null);
            StatusDot.Opacity = 1;
            _pulse = null;
        }
    }

    // ---- 输入区 ----

    private void OnComposerKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (_c.Busy)
            {
                _c.CancelAnalysis();
                e.Handled = true;
            }
            return;
        }
        if (e.Key == Key.ImeProcessed) return; // 输入法合成中的按键不拦截
        if (e.Key == Key.Return)
        {
            // Shift+Enter 换行；合成中的回车上面已拦，不误发
            if (Keyboard.Modifiers == ModifierKeys.Shift) return;
            Send();
            e.Handled = true;
        }
    }

    private void OnComposerTextChanged(object sender, TextChangedEventArgs e)
    {
        ComposerHint.Visibility = ComposerBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnSend(object sender, RoutedEventArgs e) => Send();

    private void Send()
    {
        var t = ComposerBox.Text.Trim();
        if (t.Length == 0 || _c.Busy) return;
        ComposerBox.Clear();
        _c.SendText(t);
    }
}

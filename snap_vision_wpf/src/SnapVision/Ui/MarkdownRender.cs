// 极简安全 Markdown 渲染器（markdown.ts / md_render.dart 的 C# 移植，输出 WPF 元素）。
//
// 支持子集（覆盖 GLM / DeepSeek 问答输出的习惯格式）：
//  - 标题 #~####、分隔线、围栏代码块 ```、行内代码 `x`
//  - 粗体 / 斜体 / 删除线、链接与图片（仅放行 http(s)，其余协议丢弃）
//  - 裸 URL 自动链接、引用块 >、有序/无序列表（缩进两级嵌套、中文顿号序号）
//  - GFM 表格（含 :--- 对齐）
//  - 段内单个换行 → 换行（保留 OCR 逐行结构，聊天 UI 惯例）
//
// 安全：直接构建 WPF 元素，不存在 HTML/JS 注入面。

using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using SnapVision.Ui;

namespace SnapVision.Ui;

public static class MarkdownHelper
{
    /// <summary>打开外部链接（无窗口闪烁，与 Dart 版 rundll32 等价）。</summary>
    public static void OpenUrlExternal(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch { }
    }
}

public sealed class MdBlockStyle
{
    public double FontSize { get; init; } = 13.5;
    public double LineHeight { get; init; } = 13.5 * 1.55;
    public Brush Foreground { get; init; } = AppColors.TextBrush;
}

/// <summary>一次 Markdown 解析结果（每条消息只解析一次，缓存于 ChatController）。</summary>
public sealed class MdDocument
{
    internal MdDocument(List<FrameworkElement> blocks) => Blocks = blocks;

    public List<FrameworkElement> Blocks { get; }

    public static MdDocument Parse(string src, MdBlockStyle baseStyle = null, bool noTables = false) =>
        new(Parser.ParseBlocks(src, baseStyle ?? new MdBlockStyle(), noTables));
}

internal static class Parser
{
    private static readonly Regex CodeSpan = new("(`+)([\\s\\S]*?)\\1", RegexOptions.Compiled);
    private static readonly Regex Image = new("!\\[([^\\]\\n]*)\\]\\(([^)\\s]+)\\)", RegexOptions.Compiled);
    private static readonly Regex Link = new("\\[([^\\]\\n]+)\\]\\(([^)\\s]+)\\)", RegexOptions.Compiled);
    private static readonly Regex BareUrl = new("(^|[\\s(])(https?://[^\\s<)]+)", RegexOptions.Compiled);
    private static readonly Regex BoldItalic = new("\\*{3}([^*\\n]+)\\*{3}", RegexOptions.Compiled);
    private static readonly Regex Bold = new("\\*{2}([^*\\n]+)\\*{2}", RegexOptions.Compiled);
    private static readonly Regex Italic = new("\\*([^*\\n]+)\\*", RegexOptions.Compiled);
    private static readonly Regex Strike = new("~~([^~\\n]+)~~", RegexOptions.Compiled);
    private static readonly Regex FenceStart = new("^\\s*```", RegexOptions.Compiled);
    private static readonly Regex Heading = new("^(#{1,4})\\s+(.*)$", RegexOptions.Compiled);
    private static readonly Regex HeadingTrail = new("\\s+#+\\s*$", RegexOptions.Compiled);
    private static readonly Regex Hr = new("^\\s*(?:-{3,}|\\*{3,}|_{3,})\\s*$", RegexOptions.Compiled);
    private static readonly Regex Quote = new("^\\s*>", RegexOptions.Compiled);
    private static readonly Regex QuoteStrip = new("^\\s*>\\s?", RegexOptions.Compiled);
    private static readonly Regex IndentCont = new("^ {2,}\\S", RegexOptions.Compiled);
    internal static readonly Regex ListRe = new("^(\\s*)([-*+]|\\d{1,3}[.)、])\\s+(.*)$", RegexOptions.Compiled);
    private static readonly Regex Digit = new("\\d", RegexOptions.Compiled);
    private static readonly Regex SafeUrl = new("^https?://", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Token = new("\u0000(\\d+)\u0000", RegexOptions.Compiled);

    private static readonly double[] HeadingSizes = { 15, 14, 13.5, 13.5 };

    private static string SafeUrlOrNull(string url)
    {
        var u = url.Trim();
        return SafeUrl.IsMatch(u) ? u : null;
    }

    // ---------- 行内解析 ----------

    /// <summary>行内解析：代码 span → 图片/链接 → 裸 URL → 强调（与 Dart 版同序）。</summary>
    private static List<Inline> RenderInline(string src, MdBlockStyle b)
    {
        var stash = new List<Inline>();
        string Put(Inline inline)
        {
            stash.Add(inline);
            return $"\u0000{stash.Count - 1}\u0000";
        }

        var s = src;

        // 行内代码优先：内容不再参与后续内联规则
        s = CodeSpan.Replace(s, m => Put(new Run(m.Groups[2].Value)
        {
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            Background = new SolidColorBrush(Color.FromArgb(0x61, 0, 0, 0)),
        }));

        // 图片 ![alt](url) —— 远程图片仅 http(s)
        s = Image.Replace(s, m =>
        {
            var u = SafeUrlOrNull(m.Groups[2].Value);
            if (u == null) return m.Value;
            var img = new Image { Stretch = Stretch.None };
            try
            {
                var bmp = new BitmapImage(new Uri(u));
                img.Source = bmp;
            }
            catch { /* 载入失败：不占位 */ }
            return Put(new InlineUIContainer(img));
        });

        // 链接 [text](url)
        s = Link.Replace(s, m =>
        {
            var u = SafeUrlOrNull(m.Groups[2].Value);
            if (u == null) return m.Value;
            var link = new Hyperlink(new Run(m.Groups[1].Value))
            {
                Foreground = AppColors.LinkBlueBrush,
                NavigateUri = new Uri(u),
            };
            return Put(link);
        });

        // 裸 URL 自动链接（行首或空白/左括号之后）
        s = BareUrl.Replace(s, m =>
        {
            var lead = m.Groups[1].Value;
            var url = m.Groups[2].Value;
            var link = new Hyperlink(new Run(url))
            {
                Foreground = AppColors.LinkBlueBrush,
                NavigateUri = new Uri(url),
            };
            return lead + Put(link);
        });

        List<Inline> ChildrenOf(string text)
        {
            var parts = Regex.Split(text, "\u0000\\d+\u0000");
            var result = new List<Inline>();
            var pi = 0;
            foreach (Match m in Token.Matches(text))
            {
                if (parts[pi].Length > 0) result.Add(new Run(parts[pi]));
                pi++;
                var idx = int.Parse(m.Groups[1].Value);
                result.Add(stash[idx]);
            }
            if (pi < parts.Length && parts[pi].Length > 0) result.Add(new Run(parts[pi]));
            return result;
        }

        Span Styled(IList<Inline> children, FontWeight? weight = null, FontStyle? style = null, bool strike = false)
        {
            var span = new Span();
            if (weight != null) span.FontWeight = weight.Value;
            if (style != null) span.FontStyle = style.Value;
            if (strike) span.TextDecorations = TextDecorations.Strikethrough;
            foreach (var c in children) span.Inlines.Add(c);
            return span;
        }

        // 强调（顺序：粗斜体 → 粗体 → 斜体 → 删除线）；强调可包住已 stash 的内容
        s = BoldItalic.Replace(s, m => Put(Styled(ChildrenOf(m.Groups[1].Value), FontWeights.Bold, FontStyles.Italic)));
        s = Bold.Replace(s, m => Put(Styled(ChildrenOf(m.Groups[1].Value), FontWeights.Bold)));
        s = Italic.Replace(s, m => Put(Styled(ChildrenOf(m.Groups[1].Value), style: FontStyles.Italic)));
        s = Strike.Replace(s, m => Put(Styled(ChildrenOf(m.Groups[1].Value), strike: true)));

        return ChildrenOf(s);
    }

    // ---------- 块级解析 ----------

    /// <summary>表格分隔行判定：| :--- | ---: | 形态，且至少含一个 -。</summary>
    private static bool IsTableSeparator(string line) =>
        line.Contains('-') &&
        Regex.IsMatch(line, "^\\s*\\|?(\\s*:?-{1,}:?\\s*\\|)+\\s*:?-{0,}:?\\s*\\|?\\s*$");

    /// <summary>拆表格行：去掉首尾竖线，转义 \| 后再切分。</summary>
    private static List<string> SplitTableRow(string line)
    {
        var s = line.Trim();
        if (s.StartsWith('|')) s = s[1..];
        if (s.EndsWith("|") && !s.EndsWith("\\|")) s = s[..^1];
        return s.Replace("\\|", "\u0001")
            .Split('|')
            .Select(c => c.Replace("\u0001", "|").Trim())
            .ToList();
    }

    /// <summary>判定"特殊块"起始行，段落收集时遇到即停止。</summary>
    private static bool IsBlockStart(string line, string next) =>
        FenceStart.IsMatch(line) ||
        Regex.IsMatch(line, "^#{1,4}\\s") ||
        Quote.IsMatch(line) ||
        Hr.IsMatch(line) ||
        ListRe.IsMatch(line) ||
        (line.Contains('|') && next != null && IsTableSeparator(next));

    internal static List<FrameworkElement> ParseBlocks(string src, MdBlockStyle b, bool noTables)
    {
        var lines = src.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\t", "  ").Split('\n');
        // 注意：元素只能收集到列表，不能同时挂到临时面板——挂过就有逻辑父级，
        // 返回后再挂气泡面板会抛"已是另一个元素的逻辑子元素"。
        var outList = new List<FrameworkElement>();
        void Emit(FrameworkElement e) => outList.Add(e);

        var i = 0;
        while (i < lines.Length)
        {
            var line = lines[i];

            if (line.Trim().Length == 0) { i++; continue; }

            // 围栏代码块
            if (FenceStart.IsMatch(line))
            {
                var buf = new List<string>();
                i++;
                while (i < lines.Length && !FenceStart.IsMatch(lines[i]))
                {
                    buf.Add(lines[i]);
                    i++;
                }
                i++;
                var code = new TextBlock
                {
                    Text = string.Join("\n", buf),
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 12,
                    LineHeight = 12 * 1.5,
                    LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                    Foreground = b.Foreground,
                };
                var scroll = new ScrollViewer
                {
                    Content = code,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                };
                Emit(new Border
                {
                    Margin = new Thickness(0, 6, 0, 6),
                    Padding = new Thickness(10, 8, 10, 8),
                    Background = new SolidColorBrush(Color.FromArgb(0x6B, 0, 0, 0)),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8),
                    Child = scroll,
                });
                continue;
            }

            // 标题（Dart 版此处缺 i++：遇 # 行无限循环——同款 bug 不得再搬）
            var heading = Heading.Match(line);
            if (heading.Success)
            {
                i++;
                var level = heading.Groups[1].Value.Length;
                var text = HeadingTrail.Replace(heading.Groups[2].Value, "");
                var size = HeadingSizes[level - 1];
                var tb = new TextBlock
                {
                    Margin = new Thickness(0, 10, 0, 6),
                    FontSize = size,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(Color.FromRgb(0xF0, 0xF5, 0xFC)),
                    LineHeight = size * 1.35,
                    LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                    TextWrapping = TextWrapping.Wrap,
                };
                foreach (var inline in RenderInline(text, b)) tb.Inlines.Add(inline);
                Emit(tb);
                continue;
            }

            // 分隔线
            if (Hr.IsMatch(line))
            {
                Emit(new Rectangle
                {
                    Height = 1,
                    Margin = new Thickness(0, 8, 0, 8),
                    Fill = new SolidColorBrush(Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF)),
                });
                i++;
                continue;
            }

            // 引用块（递归渲染内部内容）
            if (Quote.IsMatch(line))
            {
                var buf = new List<string>();
                while (i < lines.Length && Quote.IsMatch(lines[i]))
                {
                    buf.Add(QuoteStrip.Replace(lines[i], "", 1));
                    i++;
                }
                var inner = new StackPanel();
                foreach (var e in ParseBlocks(string.Join("\n", buf), new MdBlockStyle
                {
                    FontSize = b.FontSize,
                    LineHeight = b.LineHeight,
                    Foreground = new SolidColorBrush(Color.FromRgb(0xC6, 0xD3, 0xE6)),
                }, noTables))
                {
                    inner.Children.Add(e);
                }
                Emit(new Border
                {
                    Margin = new Thickness(0, 6, 0, 6),
                    Padding = new Thickness(10, 2, 10, 2),
                    Background = new SolidColorBrush(Color.FromArgb(0x14, 0x7F, 0xB0, 0xFF)),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(0x8C, 0x7F, 0xB0, 0xFF)),
                    BorderThickness = new Thickness(3, 0, 0, 0),
                    CornerRadius = new CornerRadius(0, 6, 6, 0),
                    Child = inner,
                });
                continue;
            }

            // 表格
            if (line.Contains('|') && i + 1 < lines.Length && IsTableSeparator(lines[i + 1]))
            {
                var header = SplitTableRow(lines[i]);
                var aligns = SplitTableRow(lines[i + 1]).Select(c =>
                {
                    var l = c.StartsWith(':');
                    var r = c.EndsWith(':');
                    return l && r ? "center" : r ? "right" : l ? "left" : "";
                }).ToList();
                i += 2;
                var rows = new List<List<string>>();
                while (i < lines.Length && lines[i].Trim().Length != 0 && lines[i].Contains('|')
                       && !IsBlockStart(lines[i], i + 1 < lines.Length ? lines[i + 1] : null))
                {
                    rows.Add(SplitTableRow(lines[i]));
                    i++;
                }

                if (noTables)
                {
                    // 诊断/兜底：表格降级为代码块文本
                    var buf = string.Join("\n",
                        new[] { header }.Concat(rows).Select(r => string.Join(" | ", r)));
                    Emit(new Border
                    {
                        Margin = new Thickness(0, 6, 0, 6),
                        Padding = new Thickness(8),
                        Background = new SolidColorBrush(Color.FromArgb(0x61, 0, 0, 0)),
                        Child = new TextBlock
                        {
                            Text = buf,
                            FontFamily = new FontFamily("Consolas"),
                            FontSize = 12,
                            Foreground = b.Foreground,
                        },
                    });
                    continue;
                }
                Emit(BuildTable(header, aligns, rows, b));
                continue;
            }

            // 列表
            var listMatch = ListRe.Match(line);
            if (listMatch.Success)
            {
                var items = new List<MdListItem>();
                while (i < lines.Length)
                {
                    var m = ListRe.Match(lines[i]);
                    if (m.Success)
                    {
                        items.Add(new MdListItem(
                            Math.Clamp(m.Groups[1].Value.Length / 2, 0, 2),
                            Digit.IsMatch(m.Groups[2].Value[0].ToString()),
                            m.Groups[3].Value));
                        i++;
                    }
                    else if (items.Count > 0 && lines[i].Trim().Length != 0 && IndentCont.IsMatch(lines[i]))
                    {
                        items[^1].Text += "\n" + lines[i].Trim();
                        i++;
                    }
                    else
                    {
                        break;
                    }
                }
                // 深度归一化：首项归 0，逐项最多加深一层
                var norm = new List<MdListItem>();
                var prevDepth = -1;
                foreach (var it in items)
                {
                    var d = norm.Count == 0 ? 0 : (it.Depth < prevDepth + 1 ? it.Depth : prevDepth + 1);
                    norm.Add(new MdListItem(d, it.Ordered, it.Text));
                    prevDepth = d;
                }
                Emit(BuildList(BuildListTree(norm), 0, b));
                continue;
            }

            // 段落：连续普通行，行间换行
            var pbuf = new List<string> { line };
            i++;
            while (i < lines.Length && lines[i].Trim().Length != 0
                   && !IsBlockStart(lines[i], i + 1 < lines.Length ? lines[i + 1] : null))
            {
                pbuf.Add(lines[i]);
                i++;
            }
            var para = new TextBlock
            {
                Margin = new Thickness(0, 0, 0, 6),
                FontSize = b.FontSize,
                LineHeight = b.LineHeight,
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                Foreground = b.Foreground,
                TextWrapping = TextWrapping.Wrap,
            };
            foreach (var inline in RenderInline(string.Join("\n", pbuf), b)) para.Inlines.Add(inline);
            Emit(para);
        }
        return outList;
    }

    private static TextAlignment AlignOf(string a) => a switch
    {
        "center" => TextAlignment.Center,
        "right" => TextAlignment.Right,
        "left" => TextAlignment.Left,
        _ => TextAlignment.Left,
    };

    private static FrameworkElement BuildTable(List<string> header, List<string> aligns,
        List<List<string>> rows, MdBlockStyle b)
    {
        var colCount = header.Count;
        var border = new SolidColorBrush(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF));

        var grid = new Grid();
        for (var c = 0; c < colCount; c++) grid.ColumnDefinitions.Add(new ColumnDefinition());

        FrameworkElement Cell(IList<Inline> inlines, bool isHeader, int col)
        {
            var tb = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = AlignOf(col < aligns.Count ? aligns[col] : ""),
                FontSize = 12.5,
                LineHeight = 12.5 * 1.5,
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                Foreground = b.Foreground,
            };
            if (isHeader)
            {
                tb.FontWeight = FontWeights.SemiBold;
                tb.Foreground = new SolidColorBrush(Color.FromRgb(0xF0, 0xF5, 0xFC));
            }
            foreach (var inline in inlines) tb.Inlines.Add(inline);
            return new Border
            {
                BorderBrush = border,
                BorderThickness = new Thickness(col > 0 ? 1 : 0, 0, 0, 0),
                Padding = new Thickness(9, 4, 9, 4),
                Child = tb,
            };
        }

        Border Row(IReadOnlyList<string> cells, bool isHeader, int? zebra)
        {
            var row = new Border
            {
                BorderBrush = border,
                BorderThickness = new Thickness(0, 1, 0, 1),
                Background = isHeader
                    ? new SolidColorBrush(Color.FromArgb(0x17, 0xFF, 0xFF, 0xFF))
                    : zebra is int z && z % 2 == 1
                        ? new SolidColorBrush(Color.FromArgb(0x0A, 0xFF, 0xFF, 0xFF))
                        : null,
            };
            var inner = new Grid();
            for (var c = 0; c < colCount; c++) inner.ColumnDefinitions.Add(new ColumnDefinition());
            for (var c = 0; c < colCount; c++)
            {
                var cell = Cell(RenderInline(c < cells.Count ? cells[c] : "", b), isHeader, c);
                Grid.SetColumn(cell, c);
                inner.Children.Add(cell);
            }
            row.Child = inner;
            return row;
        }

        var stack = new StackPanel();
        stack.Children.Add(Row(header, true, null));
        for (var r = 0; r < rows.Count; r++) stack.Children.Add(Row(rows[r], false, r));

        return new Border
        {
            Margin = new Thickness(0, 6, 0, 6),
            BorderBrush = border,
            BorderThickness = new Thickness(1, 0, 1, 0),
            CornerRadius = new CornerRadius(8),
            ClipToBounds = true,
            Child = stack,
        };
    }

    // ---------- 列表 ----------

    private sealed class MdListItem
    {
        public MdListItem(int depth, bool ordered, string text)
        {
            Depth = depth;
            Ordered = ordered;
            Text = text;
        }

        public int Depth { get; }
        public bool Ordered { get; }
        public string Text { get; set; }
    }

    private sealed class MdListNode
    {
        public MdListNode(int depth, bool ordered)
        {
            Depth = depth;
            Ordered = ordered;
        }

        public int Depth { get; }
        public bool Ordered { get; }
        public List<MdListItem> Items { get; } = new();
        public List<MdListNode> Children { get; } = new();
    }

    /// <summary>把归一化后的扁平列表还原为嵌套树。</summary>
    private static List<MdListNode> BuildListTree(List<MdListItem> items)
    {
        var roots = new List<MdListNode>();
        var stack = new List<MdListNode>();
        foreach (var it in items)
        {
            while (stack.Count > 0)
            {
                var top = stack[^1];
                if (top.Depth < it.Depth) break;
                if (top.Depth == it.Depth && top.Ordered == it.Ordered) break;
                stack.RemoveAt(stack.Count - 1);
            }
            if (stack.Count == 0)
            {
                var node = new MdListNode(it.Depth, it.Ordered);
                node.Items.Add(it);
                roots.Add(node);
                stack.Add(node);
            }
            else if (stack[^1].Depth == it.Depth)
            {
                stack[^1].Items.Add(it);
            }
            else
            {
                var node = new MdListNode(it.Depth, it.Ordered);
                node.Items.Add(it);
                stack[^1].Children.Add(node);
                stack.Add(node);
            }
        }
        return roots;
    }

    private static FrameworkElement BuildList(List<MdListNode> nodes, int level, MdBlockStyle b)
    {
        var panel = new StackPanel();
        foreach (var node in nodes)
        {
            for (var idx = 0; idx < node.Items.Count; idx++)
            {
                var marker = new TextBlock
                {
                    Text = node.Ordered ? $"{idx + 1}." : "•",
                    Width = 20,
                    FontSize = b.FontSize,
                    LineHeight = b.LineHeight,
                    LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                    Foreground = b.Foreground,
                };
                var content = new TextBlock
                {
                    FontSize = b.FontSize,
                    LineHeight = b.LineHeight,
                    LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                    Foreground = b.Foreground,
                    TextWrapping = TextWrapping.Wrap,
                };
                foreach (var inline in RenderInline(node.Items[idx].Text, b)) content.Inlines.Add(inline);
                var row = new Grid { Margin = new Thickness(20.0 * level, 0, 0, 0) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition());
                Grid.SetColumn(marker, 0);
                Grid.SetColumn(content, 1);
                row.Children.Add(marker);
                row.Children.Add(content);
                panel.Children.Add(row);
            }
            if (node.Children.Count > 0)
                panel.Children.Add(BuildList(node.Children, level + 1, b));
        }
        return panel;
    }
}

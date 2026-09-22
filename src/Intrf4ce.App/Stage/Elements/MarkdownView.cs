using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using Intrf4ce.App.Render;
using Intrf4ce.Core.Model;
using Intrf4ce.Core.Rendering;
using Markdig;
using Markdig.Extensions.Mathematics;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Avalonia.LogicalTree;
using MdBlock = Markdig.Syntax.Block;
using RenderOptions = Intrf4ce.Core.Rendering.RenderOptions;
using MdInline = Markdig.Syntax.Inlines.Inline;

namespace Intrf4ce.App.Stage.Elements;

/// <summary>
/// Markdown drawn as native text: headings, emphasis, lists, quotes, code, tables and TeX.
/// </summary>
/// <remarks>
/// Rendered from Markdig's syntax tree into Avalonia text rather than through a web view, so
/// it zooms, dims and focuses like everything else on the stage. When the text is too long
/// for its box the whole thing steps down in size, to a floor, before it is allowed to
/// scroll: a slide that needs scrolling is a slide the user will not read.
/// </remarks>
public sealed class MarkdownView : ElementView
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseGridTables()
        .UseEmphasisExtras()
        .UseAutoLinks()
        .UseTaskLists()
        .UseMathematics()
        .Build();

    private static readonly FontFamily Mono = new("Cascadia Mono, Cascadia Code, Consolas, Menlo, monospace");
    private static readonly double[] Scales = [1.0, 0.9, 0.8, 0.72, 0.65];

    private readonly ScrollViewer _scroller;
    private readonly Dictionary<string, Control> _headings = new(StringComparer.Ordinal);
    private readonly List<Task> _pending = [];
    private double _scale = 1;
    private double _fittedFor = -1;
    private CancellationToken _cancellation;

    public MarkdownView(ElementContext context, MarkdownElement element)
        : base(context, element)
    {
        _scroller = new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        };

        Children.Add(_scroller);
    }

    private double Base => 28 * _scale;

    public override IReadOnlyList<string> Parts => [.. _headings.Keys];

    public override Rect? PartBounds(string part)
    {
        if (!_headings.TryGetValue(part, out var heading) || heading.TranslatePoint(default, this) is not { } origin)
        {
            return null;
        }

        return new Rect(origin, heading.Bounds.Size).Inflate(new Thickness(12, 8, 12, 60));
    }

    /// <summary>Scrolls a heading into view, for focus cues.</summary>
    public void BringIntoView(string part)
    {
        if (_headings.TryGetValue(part, out var heading))
        {
            heading.BringIntoView();
        }
    }

    protected override async Task LoadAsync(Element element, CancellationToken cancellationToken)
    {
        _cancellation = cancellationToken;
        _scale = 1;
        _fittedFor = -1;
        Build((MarkdownElement)element);

        // Formulas render asynchronously; the element is ready when they are.
        while (_pending.Count > 0)
        {
            var batch = _pending.ToList();
            _pending.Clear();
            await Task.WhenAll(batch);
        }
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var result = base.ArrangeOverride(finalSize);

        // Step down once per size: if the content overflows at this scale, try the next.
        if (_fittedFor != finalSize.Height && _scroller.Content is Control content)
        {
            content.Measure(new Size(finalSize.Width, double.PositiveInfinity));
            if (content.DesiredSize.Height > finalSize.Height + 1)
            {
                var next = Array.FindIndex(Scales, s => s < _scale - 0.001);
                if (next >= 0)
                {
                    _scale = Scales[next];
                    Build((MarkdownElement)Element);
                    return result;
                }
            }

            _fittedFor = finalSize.Height;
        }

        return result;
    }

    private void Build(MarkdownElement element)
    {
        _headings.Clear();
        var document = Markdown.Parse(element.Text, Pipeline);
        var root = new StackPanel { Spacing = 16 * _scale };

        foreach (var block in document)
        {
            if (Render(block, 0) is { } control)
            {
                root.Children.Add(control);
            }
        }

        if (CaptionFor(element, Skin) is { } caption)
        {
            root.Children.Add(caption);
        }

        _scroller.Content = root;
        InvalidateArrange();
    }

    private Control? Render(MdBlock block, int depth) => block switch
    {
        HeadingBlock heading => Heading(heading),
        MathBlock math => DisplayMath(math),
        FencedCodeBlock or CodeBlock => Code((LeafBlock)block),
        ParagraphBlock paragraph => Paragraph(paragraph.Inline, Base, Skin.Text),
        ListBlock list => List(list, depth),
        QuoteBlock quote => Quote(quote, depth),
        ThematicBreakBlock => new Border { Height = 2, Background = Skin.Brush(Skin.Line), Margin = new Thickness(0, 8) },
        Table table => TableOf(table),
        HtmlBlock html => Paragraph(null, Base * 0.8, Skin.Muted, html.Lines.ToString()),
        ContainerBlock container => Stack(container, depth),
        _ => null,
    };

    private Control Heading(HeadingBlock heading)
    {
        var (size, weight, color) = heading.Level switch
        {
            1 => (Base * 1.75, FontWeight.Bold, Skin.Text),
            2 => (Base * 1.4, FontWeight.SemiBold, Skin.Text),
            3 => (Base * 1.15, FontWeight.SemiBold, Skin.Text),
            _ => (Base, FontWeight.SemiBold, Skin.Accent),
        };

        var text = Paragraph(heading.Inline, size, color);
        text.FontWeight = weight;
        text.Margin = new Thickness(0, heading.Level <= 2 ? 6 * _scale : 0, 0, 0);

        var slug = Slug.From(PlainText(heading.Inline), "section");
        for (var n = 2; _headings.ContainsKey(slug); n++)
        {
            slug = $"{Slug.From(PlainText(heading.Inline), "section")}-{n}";
        }

        _headings[slug] = text;
        return text;
    }

    private TextBlock Paragraph(ContainerInline? inline, double size, Color color, string? plain = null)
    {
        var block = new TextBlock
        {
            FontSize = size,
            Foreground = Skin.Brush(color),
            TextWrapping = TextWrapping.Wrap,
            LineHeight = size * 1.42,
        };

        if (plain is not null)
        {
            block.Text = plain;
        }
        else if (inline is not null && block.Inlines is { } inlines)
        {
            AddInlines(inlines, inline, size);
        }

        return block;
    }

    private void AddInlines(InlineCollection target, ContainerInline container, double size)
    {
        foreach (var inline in container)
        {
            AddInline(target, inline, size);
        }
    }

    private void AddInline(InlineCollection target, MdInline inline, double size)
    {
        switch (inline)
        {
            case LiteralInline literal:
                target.Add(new Run(literal.Content.ToString()));
                break;

            case EmphasisInline emphasis:
                Span span = emphasis.DelimiterChar switch
                {
                    '~' => new Span { TextDecorations = TextDecorations.Strikethrough },
                    '=' => new Span { Background = Skin.Brush(Skin.Accent, 0.25) },
                    _ when emphasis.DelimiterCount >= 2 => new Bold(),
                    _ => new Italic(),
                };

                AddInlines(span.Inlines, emphasis, size);
                target.Add(span);
                break;

            case CodeInline code:
                target.Add(new Run(code.Content)
                {
                    FontFamily = Mono,
                    FontSize = size * 0.88,
                    Foreground = Skin.Brush(Skin.Cool),
                    Background = Skin.Brush(Skin.Surface2),
                });
                break;

            case MathInline math:
                target.Add(InlineMath(math.Content.ToString(), size));
                break;

            case LinkInline { IsImage: true } image:
                target.Add(new Run($"[{PlainText(image)}]") { Foreground = Skin.Brush(Skin.Muted) });
                break;

            case LinkInline link:
                var linked = new Span { Foreground = Skin.Brush(Skin.Cool), TextDecorations = TextDecorations.Underline };
                AddInlines(linked.Inlines, link, size);
                target.Add(linked);
                break;

            case AutolinkInline auto:
                target.Add(new Run(auto.Url) { Foreground = Skin.Brush(Skin.Cool), TextDecorations = TextDecorations.Underline });
                break;

            case LineBreakInline { IsHard: true }:
                target.Add(new LineBreak());
                break;

            case LineBreakInline:
                target.Add(new Run(" "));
                break;

            case HtmlEntityInline entity:
                target.Add(new Run(entity.Transcoded.ToString()));
                break;

            case HtmlInline:
                break;

            case ContainerInline nested:
                AddInlines(target, nested, size);
                break;
        }
    }

    private InlineUIContainer InlineMath(string tex, double size)
    {
        var host = new ContentControl
        {
            Content = new TextBlock { Text = tex, FontFamily = Mono, FontSize = size * 0.85, Foreground = Skin.Brush(Skin.Muted) },
        };

        _pending.Add(RenderMathAsync(tex, size, display: false, host));
        return new InlineUIContainer(host);
    }

    private Control DisplayMath(MathBlock math)
    {
        var host = new ContentControl { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6) };
        _pending.Add(RenderMathAsync(math.Lines.ToString(), Base * 1.25, display: true, host));
        return host;
    }

    private async Task RenderMathAsync(string tex, double size, bool display, ContentControl host)
    {
        var result = await Context.Renderer.RenderAsync(
            RenderKind.Tex, tex.Trim(), new RenderOptions(Display: display, FontSize: size, Color: Skin.Hex(Skin.Text)), _cancellation);

        if (!result.Ok)
        {
            Warn($"TeX error in “{tex.Trim()}”: {result.Error}");
            host.Content = new TextBlock { Text = tex, FontFamily = Mono, FontSize = size * 0.8, Foreground = Skin.Brush(Skin.Danger) };
            return;
        }

        var picture = await Task.Run(() => SvgPicture.Load(result.Svg!, result.Width, result.Height));
        if (picture is null)
        {
            return;
        }

        host.Content = new SvgPictureControl
        {
            Picture = picture,
            MaxScale = 1,
            Width = result.Width,
            Height = result.Height,

            // Inline: sit on the baseline rather than on top of it.
            RenderTransform = display ? null : new TranslateTransform(0, result.Depth),
        };
    }

    private Control Code(LeafBlock block)
    {
        var text = block.Lines.ToString().TrimEnd();
        return new Border
        {
            Background = Skin.Brush(Skin.Ink, 0.75),
            BorderBrush = Skin.Brush(Skin.Line),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(20 * _scale, 14 * _scale),
            Child = new TextBlock
            {
                Text = text,
                FontFamily = Mono,
                FontSize = Base * 0.78,
                Foreground = Skin.Brush(Skin.Text, 0.92),
                TextWrapping = TextWrapping.Wrap,
                LineHeight = Base * 0.78 * 1.45,
            },
        };
    }

    private Control List(ListBlock list, int depth)
    {
        var panel = new StackPanel { Spacing = 8 * _scale, Margin = new Thickness(depth == 0 ? 4 : 0, 0, 0, 0) };
        var number = int.TryParse(list.OrderedStart, out var start) ? start : 1;

        foreach (var item in list.OfType<ListItemBlock>())
        {
            var marker = new TextBlock
            {
                Text = list.IsOrdered ? $"{number++}." : (depth % 2 == 0 ? "•" : "◦"),
                FontSize = Base,
                LineHeight = Base * 1.42,
                Foreground = Skin.Brush(list.IsOrdered ? Skin.Muted : Skin.Accent),
                Width = list.IsOrdered ? Base * 1.6 : Base * 1.1,
                FontWeight = list.IsOrdered ? FontWeight.SemiBold : FontWeight.Bold,
            };

            var content = new StackPanel { Spacing = 8 * _scale };
            foreach (var child in item)
            {
                if (Render(child, depth + 1) is { } control)
                {
                    content.Children.Add(control);
                }
            }

            var row = new DockPanel();
            DockPanel.SetDock(marker, Dock.Left);
            row.Children.Add(marker);
            row.Children.Add(content);
            panel.Children.Add(row);
        }

        return panel;
    }

    private Control Quote(QuoteBlock quote, int depth)
    {
        var inner = Stack(quote, depth);
        foreach (var text in inner.GetLogicalDescendants().OfType<TextBlock>())
        {
            text.FontStyle = FontStyle.Italic;
            text.Foreground = Skin.Brush(Skin.Muted);
        }

        return new Border
        {
            BorderBrush = Skin.Brush(Skin.Accent, 0.8),
            BorderThickness = new Thickness(4, 0, 0, 0),
            Padding = new Thickness(20 * _scale, 4, 0, 4),
            Child = inner,
        };
    }

    private StackPanel Stack(ContainerBlock container, int depth)
    {
        var panel = new StackPanel { Spacing = 12 * _scale };
        foreach (var child in container)
        {
            if (Render(child, depth) is { } control)
            {
                panel.Children.Add(control);
            }
        }

        return panel;
    }

    private Control TableOf(Table table)
    {
        var rows = table.OfType<TableRow>().ToList();
        var columns = rows.Count == 0 ? 0 : rows.Max(r => r.Count);
        var grid = new Grid();

        for (var c = 0; c < columns; c++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        }

        for (var r = 0; r < rows.Count; r++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var row = rows[r];
            for (var c = 0; c < row.Count; c++)
            {
                var cell = (TableCell)row[c];
                var inline = cell.OfType<ParagraphBlock>().FirstOrDefault()?.Inline;
                var text = Paragraph(inline, Base * 0.82, row.IsHeader ? Skin.Text : Skin.Text);
                text.FontWeight = row.IsHeader ? FontWeight.SemiBold : FontWeight.Normal;

                var border = new Border
                {
                    Padding = new Thickness(16 * _scale, 9 * _scale),
                    BorderBrush = Skin.Brush(row.IsHeader ? Skin.Accent : Skin.Line, row.IsHeader ? 0.8 : 1),
                    BorderThickness = new Thickness(0, 0, 0, row.IsHeader ? 2 : 1),
                    Background = !row.IsHeader && r % 2 == 0 ? Skin.Brush(Skin.Surface2, 0.45) : null,
                    Child = text,
                };

                Grid.SetRow(border, r);
                Grid.SetColumn(border, c);
                grid.Children.Add(border);
            }
        }

        return grid;
    }

    private static string PlainText(ContainerInline? inline)
    {
        if (inline is null)
        {
            return "";
        }

        var builder = new System.Text.StringBuilder();
        foreach (var child in inline.Descendants<LiteralInline>())
        {
            builder.Append(child.Content.ToString());
        }

        foreach (var code in inline.Descendants<CodeInline>())
        {
            builder.Append(code.Content);
        }

        return builder.ToString();
    }
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Intrf4ce.App.Render;
using Intrf4ce.Core.Model;
using Intrf4ce.Core.Rendering;

namespace Intrf4ce.App.Stage.Elements;

/// <summary>
/// A diagram, formula or chart: source compiled to SVG by the hidden renderer, drawn as vectors.
/// </summary>
public sealed class SvgElementView : ElementView
{
    private readonly SvgPictureControl _picture = new();
    private TextBlock? _caption;
    private IReadOnlyList<RenderPart> _parts = [];

    public SvgElementView(ElementContext context, Element element)
        : base(context, element)
    {
        _picture.HorizontalAlignment = HorizontalAlignment.Center;
        _picture.VerticalAlignment = VerticalAlignment.Center;
        Children.Add(_picture);
    }

    public override IReadOnlyList<string> Parts => [.. _parts.Select(p => p.Id)];

    public override Rect? PartBounds(string part)
    {
        var found = _parts.FirstOrDefault(p => p.Id == part);
        if (found is null || _picture.Picture is null)
        {
            return null;
        }

        var (scale, x, y) = _picture.PictureToControl;
        var origin = _picture.Bounds.Position;
        return new Rect(
            origin.X + x + (found.Box.X * scale),
            origin.Y + y + (found.Box.Y * scale),
            found.Box.Width * scale,
            found.Box.Height * scale);
    }

    public override Rect RegionBounds(double x, double y, double width, double height)
    {
        if (_picture.Picture is not { } picture)
        {
            return base.RegionBounds(x, y, width, height);
        }

        var (scale, ox, oy) = _picture.PictureToControl;
        var origin = _picture.Bounds.Position;
        return new Rect(
            origin.X + ox + (x * picture.Width * scale),
            origin.Y + oy + (y * picture.Height * scale),
            width * picture.Width * scale,
            height * picture.Height * scale);
    }

    protected override async Task LoadAsync(Element element, CancellationToken cancellationToken)
    {
        if (_caption is not null)
        {
            Children.Remove(_caption);
        }

        _caption = CaptionFor(element, Skin);
        if (_caption is not null)
        {
            _caption.VerticalAlignment = VerticalAlignment.Bottom;
            Children.Add(_caption);
        }

        var captionSpace = _caption is null ? 0 : 44;
        var width = double.IsNaN(Width) ? 1200 : Width;
        var height = (double.IsNaN(Height) ? 700 : Height) - captionSpace;
        _picture.Margin = new Thickness(0, 0, 0, captionSpace);

        var (kind, source, options) = element switch
        {
            DiagramElement diagram => (RenderKind.Mermaid, diagram.Mermaid, RenderOptions.Default),
            MathElement math => (RenderKind.Tex, StripDelimiters(math.Tex), new RenderOptions(FontSize: 44, Color: Skin.Hex(Skin.Text))),
            ChartElement chart => (RenderKind.VegaLite, ChartSpec.ToVegaLite(chart, width, height), new RenderOptions(Width: width, Height: height)),
            _ => throw new InvalidOperationException($"{element.TypeName} is not drawn as SVG"),
        };

        _picture.MaxScale = element switch
        {
            DiagramElement => 1.8,
            MathElement => 1.6,
            _ => 1.0,
        };

        var result = await Context.Renderer.RenderAsync(kind, source, options, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        if (!result.Ok)
        {
            _picture.Picture = null;
            _parts = [];
            Fail(Describe(element) + result.Error);
            return;
        }

        var picture = await Task.Run(() => SvgPicture.Load(result.Svg!, result.Width, result.Height), cancellationToken);
        if (picture is null)
        {
            Fail("the renderer produced SVG that could not be drawn");
            return;
        }

        _picture.Picture = picture;
        _parts = result.Parts;

        if (result.Warning is not null)
        {
            Warn(result.Warning);
        }

        if (element is DiagramElement { Steps: { } steps })
        {
            var missing = steps.Where(s => _parts.All(p => p.Id != s)).ToList();
            if (missing.Count > 0)
            {
                Warn($"steps name nodes that are not in the diagram: {string.Join(", ", missing)} (nodes: {string.Join(", ", _parts.Select(p => p.Id).Take(40))})");
            }
        }
    }

    private static string Describe(Element element) => element switch
    {
        DiagramElement => "Mermaid could not render this diagram: ",
        MathElement => "TeX error: ",
        ChartElement => "the chart could not be drawn: ",
        _ => "",
    };

    private static string StripDelimiters(string tex)
    {
        var trimmed = tex.Trim();
        foreach (var (open, close) in new[] { ("$$", "$$"), ("\\[", "\\]"), ("$", "$") })
        {
            if (trimmed.Length > open.Length + close.Length && trimmed.StartsWith(open, StringComparison.Ordinal) && trimmed.EndsWith(close, StringComparison.Ordinal))
            {
                return trimmed[open.Length..^close.Length];
            }
        }

        return trimmed;
    }
}

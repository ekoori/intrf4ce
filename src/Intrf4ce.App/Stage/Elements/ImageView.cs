using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Intrf4ce.App.Render;
using Intrf4ce.Core.Layout;
using Intrf4ce.Core.Model;

namespace Intrf4ce.App.Stage.Elements;

/// <summary>
/// An image, with callouts laid out in gutters beside it.
/// </summary>
/// <remarks>
/// When there are callouts the image gives up width for label gutters, sized to the widest
/// label on each side, so labels never cover the picture. Anchors are fractions of the image,
/// so they land on the same anatomy whatever size the image ends up drawn at.
/// </remarks>
public sealed class ImageView : ElementView
{
    private const int DecodeSide = 2560;

    private readonly Image _bitmap = new() { Stretch = Stretch.Fill };
    private readonly SvgPictureControl _vector = new() { MaxScale = 100 };
    private readonly CalloutLayer _callouts;
    private TextBlock? _caption;
    private Size _natural;
    private Rect _imageRect;

    public ImageView(ElementContext context, ImageElement element)
        : base(context, element)
    {
        _callouts = new CalloutLayer(context.Skin);
        Children.Add(_bitmap);
        Children.Add(_vector);
        Children.Add(_callouts);
    }

    private ImageElement Image => (ImageElement)Element;

    /// <summary>Where the picture is drawn, in this view's coordinates.</summary>
    public Rect ImageRect => _imageRect;

    public override IReadOnlyList<string> Parts => Image.Callouts?.Select(c => c.Id).ToList() ?? [];

    public override Rect? PartBounds(string part)
    {
        var callout = Image.Callouts?.FirstOrDefault(c => c.Id == part);
        if (callout is null || _imageRect.Width <= 0)
        {
            return null;
        }

        if (callout.Box is [var x, var y, var w, var h])
        {
            return new Rect(_imageRect.X + (x * _imageRect.Width), _imageRect.Y + (y * _imageRect.Height), w * _imageRect.Width, h * _imageRect.Height);
        }

        if (callout.At is [var ax, var ay])
        {
            // A point has no size; frame a neighbourhood around it big enough to see what it is.
            var side = Math.Min(_imageRect.Width, _imageRect.Height) * 0.28;
            var point = new Point(_imageRect.X + (ax * _imageRect.Width), _imageRect.Y + (ay * _imageRect.Height));
            return new Rect(point.X - (side / 2), point.Y - (side / 2), side, side);
        }

        return null;
    }

    /// <summary>The label of a callout, in this view's coordinates, for the spotlight to include.</summary>
    public Rect? LabelBounds(string part) => _callouts.LabelOf(part);

    public void Highlight(string? calloutId) => _callouts.Highlight(calloutId);

    protected override async Task LoadAsync(Element element, CancellationToken cancellationToken)
    {
        var image = (ImageElement)element;

        if (_caption is not null)
        {
            Children.Remove(_caption);
        }

        _caption = CaptionFor(image, Skin);
        if (_caption is not null)
        {
            Children.Add(_caption);
        }

        _callouts.Set(image.Callouts ?? []);
        InvalidateArrange();

        var loaded = await Context.Images.LoadAsync(image.Src, DecodeSide, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        _bitmap.Source = loaded.Bitmap;
        _bitmap.IsVisible = loaded.Bitmap is not null;
        _vector.Picture = loaded.Vector;
        _vector.IsVisible = loaded.Vector is not null;
        _natural = new Size(loaded.Width, loaded.Height);

        if (loaded.Width < 400 && loaded.Height < 400)
        {
            Warn($"the image is only {loaded.Width:0}×{loaded.Height:0} pixels and will look soft on a slide");
        }

        InvalidateArrange();
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var captionHeight = 0.0;
        if (_caption is not null)
        {
            _caption.Measure(new Size(finalSize.Width, finalSize.Height));
            captionHeight = _caption.DesiredSize.Height + 10;
        }

        var bounds = new Box(0, 0, finalSize.Width, Math.Max(0, finalSize.Height - captionHeight));
        var fit = Image.Fit == "cover" ? "cover" : "contain";

        var area = bounds;
        var inputs = _callouts.Inputs(bounds);
        if (inputs.Count > 0)
        {
            var (left, right) = CalloutLayout.GutterWidths(bounds, inputs);
            area = bounds.Deflate(left, 0, right, 0);
        }

        var aspect = _natural.Width > 0 && _natural.Height > 0 ? _natural.Width / _natural.Height : 16.0 / 9;
        var box = fit == "cover" ? area : area.Fit(aspect);
        _imageRect = new Rect(box.X, box.Y, box.Width, box.Height);

        _bitmap.Arrange(_imageRect);
        _vector.Arrange(_imageRect);
        _callouts.Place(_imageRect, bounds);
        _callouts.Arrange(new Rect(finalSize));
        _caption?.Arrange(new Rect(0, finalSize.Height - captionHeight + 10, finalSize.Width, captionHeight - 10));

        ClipToBounds = fit == "cover";
        return finalSize;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (var child in Children)
        {
            child.Measure(availableSize);
        }

        return availableSize;
    }
}

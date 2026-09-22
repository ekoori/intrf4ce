using System.Globalization;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Transformation;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Intrf4ce.App.Media;
using Intrf4ce.App.Render;
using Intrf4ce.Core.Layout;
using Intrf4ce.Core.Model;
using Intrf4ce.Media.Images;

namespace Intrf4ce.App.Stage.Elements;

/// <summary>
/// Photos as a pile of prints, justified rows or a timeline, each one saying on hover what its
/// metadata knows.
/// </summary>
/// <remarks>
/// Prints are decoded at about the size the slide shows them. The one in focus is decoded again
/// at full size, because the camera is about to fill the screen with it, and let go when focus
/// moves on. The pile is seeded from the element's id, so it falls the same way every time the
/// slide is shown.
/// </remarks>
public sealed class GalleryView : ElementView
{
    private const int MaxPhotos = 80;
    private const int FocusSide = 2560;

    private static readonly SemaphoreSlim Decoding = new(Math.Clamp(Environment.ProcessorCount / 2, 2, 6));

    private readonly List<Print> _prints = [];
    private readonly TimelineAxis _axis;
    private TextBlock? _caption;
    private Print? _focused;
    private IReadOnlyList<PhotoPlacement> _placements = [];
    private Size _laidOutFor;
    private bool _dirty = true;
    private CancellationTokenSource? _sharpening;

    public GalleryView(ElementContext context, GalleryElement element)
        : base(context, element)
    {
        _axis = new TimelineAxis(context.Skin) { IsHitTestVisible = false, IsVisible = false };
        Children.Add(_axis);
    }

    private GalleryElement Gallery => (GalleryElement)Element;

    private string Arrangement => Gallery.Arrange ?? "scatter";

    public override IReadOnlyList<string> Parts => [.. _prints.Select(p => p.PartId)];

    public override Rect? PartBounds(string part)
    {
        var index = _prints.FindIndex(p => p.PartId == part);
        if (index < 0 || index >= _placements.Count)
        {
            return null;
        }

        // A print in focus is straightened, so it is framed as it will then be drawn.
        var box = _prints[index].Focused ? _placements[index].Box : _placements[index].Covered;
        return new Rect(box.X, box.Y, box.Width, box.Height);
    }

    public override string? PartAt(Point point) =>
        this.InputHitTest(point) is Visual hit && hit.FindAncestorOfType<Print>(includeSelf: true) is { } print ? print.PartId : null;

    public override string Describe(string? part) =>
        _prints.FirstOrDefault(p => p.PartId == part) is { } print
            ? print.Caption ?? Path.GetFileName(print.Decoded?.Path ?? print.Source)
            : Gallery.Caption ?? "these photos";

    public override void Highlight(string? part)
    {
        var print = part is null ? null : _prints.FirstOrDefault(p => p.PartId == part);
        if (print == _focused)
        {
            return;
        }

        if (_focused is { } previous)
        {
            previous.Focused = false;
            previous.Soften();
        }

        _sharpening?.Cancel();
        _sharpening = null;
        _focused = print;

        if (print is not null)
        {
            print.Focused = true;
            _sharpening = new CancellationTokenSource();
            _ = SharpenAsync(print, _sharpening.Token);
        }
    }

    protected override async Task LoadAsync(Element element, CancellationToken cancellationToken)
    {
        var gallery = (GalleryElement)element;

        if (_caption is not null)
        {
            Children.Remove(_caption);
        }

        _caption = CaptionFor(gallery, Skin);
        if (_caption is not null)
        {
            Children.Add(_caption);
        }

        var photos = await CollectAsync(gallery, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        // Keep the prints that are still there, so a change to one caption does not reload the rest.
        var kept = _prints.ToDictionary(p => (p.PartId, p.Source));
        foreach (var print in _prints.Where(p => !photos.Any(f => f.Id == p.PartId && f.Source == p.Source)))
        {
            Children.Remove(print);
        }

        _prints.Clear();
        foreach (var photo in photos)
        {
            if (!kept.TryGetValue((photo.Id, photo.Source), out var print))
            {
                print = new Print(photo.Id, photo.Source, Skin);
                Children.Add(print);
            }

            print.Caption = photo.Caption;
            print.Facts = photo.Facts ?? print.Facts;
            _prints.Add(print);
        }

        if (_focused is not null && !_prints.Contains(_focused))
        {
            _focused = null;
        }

        if (photos.Count == 0)
        {
            if (Issues.Count == 0)
            {
                Fail(Gallery.Folder is { } empty ? $"there are no images in {ExpandFolder(empty)}" : "there are no photos to show");
            }

            return;
        }

        var side = photos.Count switch
        {
            1 => 2048,
            <= 4 => 1280,
            <= 12 => 800,
            _ => 560,
        };

        var failures = 0;
        await Task.WhenAll(_prints.Where(p => p.Decoded is null || p.Side != side).Select(async print =>
        {
            await Decoding.WaitAsync(cancellationToken);
            try
            {
                var loaded = await Context.Images.LoadAsync(print.Source, side, cancellationToken);
                var facts = print.Facts ?? await Task.Run(() => PhotoMetadata.Read(loaded.Path), cancellationToken);
                print.Show(loaded, facts, side);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Interlocked.Increment(ref failures);
                print.ShowBroken();
                Warn($"photo '{print.PartId}' ({print.Source}) could not be shown: {ex.Message}");
            }
            finally
            {
                Decoding.Release();
            }
        }));

        cancellationToken.ThrowIfCancellationRequested();

        if (failures > 0 && failures == _prints.Count)
        {
            Fail("none of the photos could be shown");
        }

        if (Arrangement == "timeline")
        {
            // Oldest first; photos that do not know their date go at the end, in the order given.
            var ordered = _prints.OrderBy(p => p.Facts?.Taken is null ? 1 : 0).ThenBy(p => p.Facts?.Taken).ToList();
            _prints.Clear();
            _prints.AddRange(ordered);
        }

        _dirty = true;
        InvalidateMeasure();

        // Dealt onto the table one after another, once every print knows its shape.
        var delay = 0;
        foreach (var print in _prints.Where(p => !p.Revealed))
        {
            print.Reveal(TimeSpan.FromMilliseconds(delay));
            delay += Math.Max(12, 360 / _prints.Count);
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var size = new Size(
            double.IsInfinity(availableSize.Width) ? Width : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? Height : availableSize.Height);

        _caption?.Measure(size);
        if (_dirty || size != _laidOutFor)
        {
            Layout(size);
        }

        for (var i = 0; i < _prints.Count && i < _placements.Count; i++)
        {
            var box = _placements[i].Box;
            _prints[i].Measure(new Size(box.Width, box.Height));
        }

        _axis.Measure(size);
        foreach (var child in Children.Where(c => c is not Print && c != _axis && c != _caption))
        {
            child.Measure(size);
        }

        return size;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (_dirty || finalSize != _laidOutFor)
        {
            Layout(finalSize);
        }

        for (var i = 0; i < _prints.Count; i++)
        {
            if (i < _placements.Count)
            {
                var placement = _placements[i];
                var box = new Rect(placement.Box.X, placement.Box.Y, placement.Box.Width, placement.Box.Height);
                _prints[i].Place(placement.Rotation, i, box.Size);
                _prints[i].Arrange(box);
            }
        }

        _axis.Arrange(new Rect(finalSize));
        if (_caption is not null)
        {
            var height = _caption.DesiredSize.Height;
            _caption.Arrange(new Rect(0, finalSize.Height - height, finalSize.Width, height));
        }

        foreach (var child in Children.Where(c => c is not Print && c != _axis && c != _caption))
        {
            child.Arrange(new Rect(finalSize));
        }

        return finalSize;
    }

    private void Layout(Size size)
    {
        _dirty = false;
        _laidOutFor = size;

        var captionHeight = _caption is null ? 0 : _caption.DesiredSize.Height + 10;
        var area = new Box(0, 0, size.Width, Math.Max(0, size.Height - captionHeight));
        var aspects = _prints.Select(p => p.Aspect).ToList();

        switch (Arrangement)
        {
            case "grid":
                _placements = GalleryLayout.Justified(area, aspects);
                _axis.IsVisible = false;
                break;

            case "timeline":
                var (placements, axis) = GalleryLayout.Timeline(area, aspects);
                _placements = placements;
                _axis.Set(axis, area, [.. _prints.Select((p, i) => (placements[i], p.Facts?.Taken))]);
                _axis.IsVisible = true;
                break;

            default:
                _placements = GalleryLayout.Scatter(area, aspects, Seed(Id));
                _axis.IsVisible = false;
                break;
        }
    }

    private async Task SharpenAsync(Print print, CancellationToken cancellationToken)
    {
        try
        {
            var loaded = await Context.Images.LoadAsync(print.Source, FocusSide, cancellationToken, cache: false);
            if (!cancellationToken.IsCancellationRequested && print.Focused)
            {
                print.Sharpen(loaded);
            }
            else
            {
                loaded.Bitmap?.Dispose();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The print is already on screen at a smaller size; that will do.
            Log.Write($"gallery '{Id}': could not load {print.Source} at full size: {ex.Message}");
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>The photos to show: the agent's items, then the folder's images, oldest first.</summary>
    private async Task<List<Photo>> CollectAsync(GalleryElement gallery, CancellationToken cancellationToken)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        var photos = new List<Photo>();

        if (gallery.Items is { Count: > 0 } items)
        {
            var ids = ElementParts.GalleryItems(items, used);
            photos.AddRange(items.Select((item, i) => new Photo(ids[i], item.Src, item.Caption, null)));
        }

        if (!string.IsNullOrWhiteSpace(gallery.Folder))
        {
            var folder = ExpandFolder(gallery.Folder);
            if (!Directory.Exists(folder))
            {
                if (photos.Count == 0)
                {
                    Fail($"there is no folder at {folder}");
                    return [];
                }

                Warn($"there is no folder at {folder}; showing the items only");
            }
            else
            {
                var found = await Task.Run(() => Scan(folder, cancellationToken), cancellationToken);
                foreach (var (path, facts) in found)
                {
                    photos.Add(new Photo(ElementParts.Photo(Path.GetFileName(path), used), path, null, facts));
                }
            }
        }

        if (photos.Count > MaxPhotos)
        {
            Warn($"{photos.Count} photos is more than one slide can show; showing the first {MaxPhotos}. Split them across slides, or pick the best with scan_media.");
            photos.RemoveRange(MaxPhotos, photos.Count - MaxPhotos);
        }

        return photos;
    }

    private static List<(string Path, PhotoFacts Facts)> Scan(string folder, CancellationToken cancellationToken)
    {
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
        };

        var found = new List<(string Path, PhotoFacts Facts)>();
        foreach (var file in Directory.EnumerateFiles(folder, "*", options).Where(ImageDecoder.IsImage).Take(MaxPhotos * 4))
        {
            cancellationToken.ThrowIfCancellationRequested();
            found.Add((file, PhotoMetadata.Read(file)));
        }

        return [.. found
            .OrderBy(f => f.Facts.Taken ?? DateTimeOffset.MaxValue)
            .ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase)];
    }

    private static string ExpandFolder(string folder)
    {
        var expanded = Environment.ExpandEnvironmentVariables(folder.Trim());
        if (expanded.StartsWith('~'))
        {
            expanded = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + expanded[1..];
        }

        return Path.GetFullPath(expanded);
    }

    /// <summary>A hash that is the same in every run, unlike <see cref="string.GetHashCode()"/>.</summary>
    private static int Seed(string text)
    {
        unchecked
        {
            var hash = (int)2166136261;
            foreach (var c in text)
            {
                hash = (hash ^ c) * 16777619;
            }

            return hash;
        }
    }

    private sealed record Photo(string Id, string Source, string? Caption, PhotoFacts? Facts);

    /// <summary>One photo on the table: a paper border, the picture, and a band of facts shown on hover.</summary>
    private sealed class Print : Border
    {
        private static readonly Color Paper = Color.FromRgb(0xF4, 0xF1, 0xEA);

        private readonly Skin _skin;
        private readonly Image _image = new() { Stretch = Stretch.UniformToFill };
        private readonly SvgPictureControl _vector = new() { MaxScale = 100, IsVisible = false };
        private readonly Border _band;
        private readonly TextBlock _title;
        private readonly TextBlock _facts;
        private Bitmap? _sharp;
        private string? _transform;
        private double _rotation;
        private int _order;
        private bool _hover;
        private bool _focused;

        public Print(string partId, string source, Skin skin)
        {
            _skin = skin;
            PartId = partId;
            Source = source;

            Background = new SolidColorBrush(Paper);
            CornerRadius = new CornerRadius(3);
            BoxShadow = Shadow(lifted: false);
            Cursor = new Cursor(StandardCursorType.Hand);
            Opacity = 0;

            _title = new TextBlock
            {
                Foreground = Brushes.White,
                FontWeight = FontWeight.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                MaxLines = 2,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };

            _facts = new TextBlock
            {
                Foreground = new SolidColorBrush(Colors.White, 0.78),
                TextWrapping = TextWrapping.Wrap,
                MaxLines = 2,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };

            _band = new Border
            {
                VerticalAlignment = VerticalAlignment.Bottom,
                Background = new LinearGradientBrush
                {
                    StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                    EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                    GradientStops =
                    {
                        new GradientStop(Color.FromArgb(0, 0, 0, 0), 0),
                        new GradientStop(Color.FromArgb(200, 0, 0, 0), 0.45),
                        new GradientStop(Color.FromArgb(225, 0, 0, 0), 1),
                    },
                },
                Opacity = 0,
                IsHitTestVisible = false,
                Child = new StackPanel { Spacing = 2, Children = { _title, _facts } },
                Transitions = [new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(200) }],
            };

            var inside = new Panel { ClipToBounds = true, Children = { _image, _vector, _band } };
            Child = inside;

            Transitions =
            [
                new TransformOperationsTransition { Property = RenderTransformProperty, Duration = TimeSpan.FromMilliseconds(220), Easing = new CubicEaseOut() },
                new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(380) },
            ];

            PointerEntered += (_, _) => SetHover(true);
            PointerExited += (_, _) => SetHover(false);
        }

        public string PartId { get; }

        public string Source { get; }

        public string? Caption { get; set; }

        public PhotoFacts? Facts { get; set; }

        public LoadedImage? Decoded { get; private set; }

        /// <summary>The decode size of what is showing, so a gallery that grows reloads at the new size.</summary>
        public int Side { get; private set; }

        public double Aspect => Decoded is { Width: > 0, Height: > 0 } loaded ? loaded.Width / loaded.Height
            : Facts is { Width: > 0, Height: > 0 } facts ? facts.Width / (double)facts.Height
            : 1.5;

        public bool Focused
        {
            get => _focused;
            set
            {
                _focused = value;
                Refresh();
            }
        }

        public void Show(LoadedImage loaded, PhotoFacts facts, int side)
        {
            Decoded = loaded;
            Facts = facts;
            Side = side;

            if (_sharp is null)
            {
                _image.Source = loaded.Bitmap;
            }

            _image.IsVisible = loaded.Bitmap is not null;
            _vector.Picture = loaded.Vector;
            _vector.IsVisible = loaded.Vector is not null;
            UpdateText();
        }

        public void ShowBroken()
        {
            Background = new SolidColorBrush(_skin.Surface2);
            _title.Text = "✕ " + Path.GetFileName(Source);
            _band.Opacity = 1;
        }

        public bool Revealed { get; private set; }

        public void Reveal(TimeSpan delay)
        {
            Revealed = true;
            if (delay <= TimeSpan.Zero)
            {
                Opacity = 1;
            }
            else
            {
                DispatcherTimer.RunOnce(() => Opacity = 1, delay);
            }
        }

        /// <summary>Swaps in the full-size decode while this print is in focus.</summary>
        public void Sharpen(LoadedImage loaded)
        {
            if (loaded.Bitmap is null)
            {
                return;
            }

            _sharp?.Dispose();
            _sharp = loaded.Bitmap;
            _image.Source = _sharp;
        }

        /// <summary>Back to the small decode, and the large one freed.</summary>
        public void Soften()
        {
            if (_sharp is null)
            {
                return;
            }

            _image.Source = Decoded?.Bitmap;
            _sharp.Dispose();
            _sharp = null;
        }

        /// <summary>Where the layout put it: its turn on the table and its place in the pile.</summary>
        public void Place(double rotation, int order, Size size)
        {
            _rotation = rotation;
            _order = order;

            var frame = Math.Clamp(Math.Min(size.Width, size.Height) * 0.035, 3, 14);
            Padding = new Thickness(frame);
            var text = Math.Clamp(size.Height * 0.03, 11, 22);
            _title.FontSize = text;
            _facts.FontSize = text * 0.8;
            _band.Padding = new Thickness(text * 0.8, text * 2.2, text * 0.8, text * 0.7);
            Refresh();
        }

        private void SetHover(bool hover)
        {
            _hover = hover;
            Refresh();
        }

        private void Refresh()
        {
            var lifted = _hover || _focused;
            ZIndex = _focused ? 20_000 : _hover ? 10_000 : _order;

            // A lifted print straightens a little, as a hand picking it up would turn it.
            var rotation = _focused ? 0 : _hover ? _rotation * 0.4 : _rotation;
            var scale = _hover && !_focused ? 1.04 : 1;
            var transform = string.Create(CultureInfo.InvariantCulture, $"rotate({rotation:0.###}deg) scale({scale:0.###})");
            if (transform != _transform)
            {
                // Set only on a change: every new value restarts the transition.
                _transform = transform;
                RenderTransform = TransformOperations.Parse(transform);
            }

            BoxShadow = Shadow(lifted);
            _band.Opacity = lifted && (_title.Text is { Length: > 0 } || _facts.Text is { Length: > 0 }) ? 1 : 0;
        }

        private void UpdateText()
        {
            _title.Text = Caption ?? Path.GetFileName(Decoded?.Path ?? Source);
            _facts.Text = Facts?.Summary();
            _facts.IsVisible = !string.IsNullOrEmpty(_facts.Text);
        }

        private static BoxShadows Shadow(bool lifted) => new(new BoxShadow
        {
            Blur = lifted ? 36 : 14,
            OffsetY = lifted ? 14 : 5,
            Color = Color.FromArgb(lifted ? (byte)170 : (byte)120, 0, 0, 0),
        });
    }

    /// <summary>The line through a timeline, with each photo's date where it meets the line.</summary>
    private sealed class TimelineAxis(Skin skin) : Control
    {
        private readonly Typeface _face = new(skin.UiFont);
        private double _axis;
        private Box _area;
        private IReadOnlyList<(PhotoPlacement Placement, DateTimeOffset? Taken)> _marks = [];

        public void Set(double axis, Box area, IReadOnlyList<(PhotoPlacement Placement, DateTimeOffset? Taken)> marks)
        {
            _axis = axis;
            _area = area;
            _marks = marks;
            InvalidateVisual();
        }

        public override void Render(DrawingContext context)
        {
            if (_marks.Count == 0)
            {
                return;
            }

            var line = new Pen(skin.Brush(skin.Line), 2);
            context.DrawLine(line, new Point(_area.X, _axis), new Point(_area.Right, _axis));

            // As much of the date as tells the photos apart: times within a day, days within a year.
            var dated = _marks.Where(m => m.Taken is not null).Select(m => m.Taken!.Value).ToList();
            var format = dated.Count > 1 && dated.All(d => d.Date == dated[0].Date) ? "HH:mm"
                : dated.Count > 0 && dated.All(d => d.Year == dated[0].Year) ? "d MMM"
                : "d MMM yyyy";

            var leader = new Pen(skin.Brush(skin.Cool, 0.55), 1.5);
            foreach (var (placement, taken) in _marks)
            {
                var box = placement.Box;
                var x = box.Center.X;
                var above = box.Bottom <= _axis;
                context.DrawLine(leader, new Point(x, _axis), new Point(x, above ? box.Bottom : box.Y));
                context.DrawEllipse(skin.Brush(skin.Cool), null, new Point(x, _axis), 5, 5);

                var label = new FormattedText(
                    taken?.ToString(format, CultureInfo.CurrentCulture) ?? "undated",
                    CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight,
                    _face,
                    14,
                    skin.Brush(taken is null ? skin.Faint : skin.Muted));

                // On the other side of the line from the photo, where there is room.
                var y = above ? _axis + 4 : _axis - 4 - label.Height;
                context.DrawText(label, new Point(x - (label.Width / 2), y));
            }
        }
    }
}

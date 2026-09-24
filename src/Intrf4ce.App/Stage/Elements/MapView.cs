using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Intrf4ce.Core.Layout;
using Intrf4ce.Core.Model;
using Intrf4ce.Media.Images;

namespace Intrf4ce.App.Stage.Elements;

/// <summary>
/// A map, with the places the narration walks through marked on it.
/// </summary>
/// <remarks>
/// The map moves, rather than the slide holding a picture of one: focusing a place flies to
/// it, which is what a presentation about a journey, a campaign or a river actually needs. The
/// frame is set the moment a place is focused and the stage's own camera carries the motion;
/// tiles for the new frame arrive a moment later, and until they do the ones already loaded
/// are drawn scaled, so there is always a map rather than a hole.
/// <para>
/// Tiles are OpenStreetMap's, which is why the credit in the corner is drawn by this class and
/// not left to the agent to remember.
/// </para>
/// </remarks>
public sealed class MapView : ElementView
{
    private const double MarkerRadius = 9;

    private readonly TileLayer _tiles;
    private readonly Border _credit;
    private readonly List<(string Id, Place Place, MapMarker Marker)> _places = [];
    private readonly Dictionary<string, Bitmap> _loaded = new(StringComparer.Ordinal);
    private TextBlock? _caption;
    private MapFrame _frame = new(new LatLon(20, 0), 1);
    private CancellationTokenSource? _fetching;
    private TaskCompletionSource _drawn = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private (MapFrame Frame, Size Area)? _asked;
    private Size _fittedFor;
    private string? _focused;
    private Rect _area;

    public MapView(ElementContext context, MapElement element)
        : base(context, element)
    {
        _tiles = new TileLayer(_loaded, Skin);

        _credit = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(180, 0xF7, 0xF5, 0xF0)),
            Padding = new Thickness(7, 2),
            CornerRadius = new CornerRadius(4),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            IsHitTestVisible = false,
            Child = new TextBlock
            {
                Text = TileSource.Attribution,
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(0x33, 0x3A, 0x44)),
            },
        };

        Children.Add(_tiles);
        Children.Add(_credit);
        ClipToBounds = true;
    }

    private MapElement Map => (MapElement)Element;

    public override IReadOnlyList<string> Parts => [.. _places.Select(p => p.Id)];

    public override Rect? PartBounds(string part)
    {
        if (_places.FirstOrDefault(p => p.Id == part) is not { Marker: not null } place || _area.Width <= 0)
        {
            return null;
        }

        var at = Screen(Where(place.Place));

        // A marked place has no size of its own; frame what is around it.
        var side = Math.Min(_area.Width, _area.Height) * 0.34;
        return new Rect(at.X - (side / 2), at.Y - (side / 2), side, side);
    }

    public override Rect? LabelBounds(string part) =>
        _places.FirstOrDefault(p => p.Id == part) is { Marker: { } marker } && marker.Bounds.Width > 0 ? marker.Bounds : null;

    /// <summary>Only the markers take clicks; the map itself is scenery.</summary>
    public override string? PartAt(Point point) =>
        _places.FirstOrDefault(p => p.Marker.Bounds.Contains(point)).Id;

    public override string Describe(string? part)
    {
        if (part is null)
        {
            return Map.Caption ?? (_places.Count > 0 ? "this map of " + string.Join(", ", _places.Take(3).Select(p => p.Place.Label)) : "this map");
        }

        var place = _places.FirstOrDefault(p => p.Id == part).Place;
        return place is null ? part : place.Detail is { Length: > 0 } detail ? $"{place.Label} — {detail}" : place.Label;
    }

    /// <summary>
    /// Flies the map to a place, or back to the whole picture when nothing is focused.
    /// </summary>
    /// <remarks>
    /// The map's own zoom, not the stage camera's: zooming a picture of the world does not show
    /// you the streets of Cairo, and fetching the tiles that do is the only way to get them.
    /// </remarks>
    public override void Highlight(string? part)
    {
        _focused = part;

        foreach (var (id, _, marker) in _places)
        {
            marker.Lit = id == part;
        }

        var place = part is null ? null : _places.FirstOrDefault(p => p.Id == part).Place;
        _frame = place?.At is [var latitude, var longitude]
            ? new MapFrame(new LatLon(latitude, longitude), Math.Clamp(place.Zoom ?? Closer(), WebMercator.MinZoom, WebMercator.MaxZoom))
            : Framing();

        InvalidateArrange();
    }

    protected override async Task LoadAsync(Element element, CancellationToken cancellationToken)
    {
        var map = (MapElement)element;

        foreach (var (_, _, marker) in _places)
        {
            Children.Remove(marker);
        }

        _places.Clear();
        if (_caption is not null)
        {
            Children.Remove(_caption);
            _caption = null;
        }

        var places = map.Places ?? [];
        var ids = ElementParts.Places(places);
        for (var i = 0; i < places.Count; i++)
        {
            var marker = new MapMarker(Skin, places[i].Label, i + 1, places.Count > 1 && map.Route);
            _places.Add((ids[i], places[i], marker));
            Children.Add(marker);
        }

        _caption = CaptionFor(map, Skin);
        if (_caption is not null)
        {
            Children.Add(_caption);
        }

        // The credit goes on top of the tiles, and the markers on top of that.
        Children.Remove(_credit);
        Children.Add(_credit);

        _asked = null;
        _fittedFor = default;
        _drawn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _frame = Framing();
        InvalidateArrange();

        // The tiles are asked for once the element has been given its size, which has not
        // happened yet; waiting for them here is what keeps put_slide from reporting a map
        // that is still a blue rectangle. A map is not worth hanging a slide on, so a network
        // that never answers becomes a warning rather than a wait.
        try
        {
            await _drawn.Task.WaitAsync(TimeSpan.FromSeconds(12), cancellationToken).ConfigureAwait(true);
        }
        catch (TimeoutException)
        {
            Warn("the map is still loading its tiles");
        }
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var captionHeight = 0.0;
        if (_caption is not null)
        {
            _caption.Measure(finalSize);
            captionHeight = _caption.DesiredSize.Height + 14;
        }

        _area = new Rect(0, 0, finalSize.Width, Math.Max(0, finalSize.Height - captionHeight));

        // The frame that fits the places depends on the shape of the space, which is only
        // known here; a map that is focused on one place keeps the frame that focus chose.
        if (_focused is null && _area.Size != _fittedFor)
        {
            _fittedFor = _area.Size;
            _frame = Framing();
        }

        _tiles.Arrange(_area);
        _tiles.Frame(_frame, _area.Size);
        _tiles.Route = Map.Route ? [.. _places.Select(p => Screen(Where(p.Place)))] : [];

        foreach (var (id, place, marker) in _places)
        {
            var at = Screen(Where(place));
            marker.Measure(finalSize);
            var size = marker.DesiredSize;

            // The pin points at the place; the label sits above it, and flips below near the top.
            var above = at.Y - size.Height - MarkerRadius > _area.Y + 4;
            var x = Math.Clamp(at.X - (size.Width / 2), _area.X + 2, Math.Max(_area.X + 2, _area.Right - size.Width - 2));
            var y = above ? at.Y - size.Height - MarkerRadius : at.Y + MarkerRadius;
            marker.Arrange(new Rect(x, y, size.Width, size.Height));
            marker.PointsAt(new Point(at.X - x, at.Y - y));
            marker.IsVisible = _area.Contains(at);
            marker.Lit = id == _focused;
        }

        _credit.Measure(finalSize);
        _credit.Arrange(_area);
        _caption?.Arrange(new Rect(0, finalSize.Height - captionHeight + 14, finalSize.Width, captionHeight - 14));

        // Asking from here rather than from wherever the frame was set: this is the only place
        // that knows how much map there is to cover.
        if (_asked is not { } asked || asked.Frame != _frame || asked.Area != _area.Size)
        {
            _asked = (_frame, _area.Size);
            Request();
        }

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

    /// <summary>Where the map sits when nothing in particular is focused.</summary>
    private MapFrame Framing()
    {
        var size = _area.Width > 0 ? _area : new Rect(0, 0, 900, 600);

        if (Map.Center is [var latitude, var longitude])
        {
            return new MapFrame(new LatLon(latitude, longitude), Math.Clamp(Map.Zoom ?? 4, WebMercator.MinZoom, WebMercator.MaxZoom));
        }

        var places = _places.Select(p => Where(p.Place)).ToList();
        var fitted = WebMercator.Fit(places, size.Width, size.Height);
        return Map.Zoom is { } zoom ? fitted with { Zoom = Math.Clamp(zoom, WebMercator.MinZoom, WebMercator.MaxZoom) } : fitted;
    }

    /// <summary>How close in to go for one place, when the agent did not say.</summary>
    private double Closer() => Math.Clamp(Math.Max(_frame.Zoom + 3, 9), WebMercator.MinZoom, 16);

    private static LatLon Where(Place place) =>
        place.At is [var latitude, var longitude] ? new LatLon(latitude, longitude) : new LatLon(0, 0);

    /// <summary>Where a place falls in this view's coordinates, for the frame as it is now.</summary>
    private Point Screen(LatLon place)
    {
        var zoom = _frame.Zoom;
        var centre = WebMercator.ToWorld(_frame.Centre, zoom);
        var at = WebMercator.ToWorld(place, zoom);

        // Across the date line the short way: a place 350 degrees east is ten degrees west.
        var size = WebMercator.WorldSize(zoom);
        var dx = at.X - centre.X;
        dx -= Math.Round(dx / size) * size;

        return new Point(_area.X + (_area.Width / 2) + dx, _area.Y + (_area.Height / 2) + (at.Y - centre.Y));
    }

    private void Request() => _ = RequestAsync(CancellationToken.None);

    /// <summary>Fetches every tile the current frame needs, and draws them as they arrive.</summary>
    private async Task RequestAsync(CancellationToken cancellationToken)
    {
        if (_area.Width <= 0 || _area.Height <= 0)
        {
            return;
        }

        _fetching?.Cancel();
        var fetching = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _fetching = fetching;

        var zoom = (int)Math.Round(_frame.Zoom);
        var centre = WebMercator.ToWorld(_frame.Centre, zoom);
        var left = centre.X - (_area.Width / 2);
        var top = centre.Y - (_area.Height / 2);

        var wanted = TileSource.Covering(zoom, left, top, _area.Width, _area.Height)
            .Select(t => t.Tile)
            .Where(t => !_loaded.ContainsKey(t.Key))
            .ToList();

        // All at once, so a screen of tiles is not a screen of round trips; the tile source's
        // own limit is what keeps this polite to the servers giving them away.
        var failures = 0;
        await Task.WhenAll(wanted.Select(Fetch)).ConfigureAwait(true);

        async Task Fetch(Tile tile)
        {
            try
            {
                var bytes = await Context.Tiles.GetAsync(tile, fetching.Token).ConfigureAwait(true);
                using var stream = new MemoryStream(bytes);
                _loaded[tile.Key] = new Bitmap(stream);
                Forget();
                _tiles.InvalidateVisual();
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or ArgumentException)
            {
                // One missing square is a patch of sea; a whole map of them is worth reporting.
                if (Interlocked.Increment(ref failures) == 1)
                {
                    Log.Write($"map tile {tile.Key}: {ex.Message}");
                }
            }
        }

        if (fetching.IsCancellationRequested)
        {
            return;
        }

        if (failures > 0 && failures == wanted.Count && wanted.Count > 0)
        {
            Warn("the map tiles could not be fetched; is this machine offline?");
        }

        _drawn.TrySetResult();
    }

    /// <summary>Keeps the picture of the world from growing without end.</summary>
    private void Forget()
    {
        const int Keep = 320;
        if (_loaded.Count <= Keep)
        {
            return;
        }

        foreach (var key in _loaded.Keys.Take(_loaded.Count - Keep).ToList())
        {
            _loaded[key].Dispose();
            _loaded.Remove(key);
        }
    }

    /// <summary>The tiles themselves, drawn under everything else.</summary>
    private sealed class TileLayer(Dictionary<string, Bitmap> loaded, Skin skin) : Control
    {
        private readonly IBrush _sea = new SolidColorBrush(Color.FromRgb(0xA6, 0xC8, 0xE0));
        private MapFrame _frame = new(new LatLon(20, 0), 1);
        private double _left;
        private double _top;
        private int _zoom;

        /// <summary>The places, in this control's coordinates, for the line between them.</summary>
        public IReadOnlyList<Point> Route { get; set; } = [];

        public void Frame(MapFrame frame, Size size)
        {
            _frame = frame;
            _zoom = (int)Math.Round(frame.Zoom);
            var centre = WebMercator.ToWorld(frame.Centre, _zoom);
            _left = centre.X - (size.Width / 2);
            _top = centre.Y - (size.Height / 2);
            InvalidateVisual();
        }

        public override void Render(DrawingContext context)
        {
            ArgumentNullException.ThrowIfNull(context);

            var size = Bounds.Size;
            context.FillRectangle(_sea, new Rect(size));

            foreach (var (tile, x, y) in TileSource.Covering(_zoom, _left, _top, size.Width, size.Height))
            {
                var at = new Rect(x, y, WebMercator.TileSize, WebMercator.TileSize);
                if (loaded.TryGetValue(tile.Key, out var bitmap))
                {
                    context.DrawImage(bitmap, at);
                }
                else
                {
                    DrawWhatWeHave(context, tile, at);
                }
            }

            if (Route.Count > 1)
            {
                var pen = new Pen(skin.Brush(skin.Accent, 0.9), 4, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
                var line = new StreamGeometry();
                using (var draw = line.Open())
                {
                    draw.BeginFigure(Route[0], isFilled: false);
                    foreach (var point in Route.Skip(1))
                    {
                        draw.LineTo(point);
                    }

                    draw.EndFigure(isClosed: false);
                }

                context.DrawGeometry(null, pen, line);
            }

            _ = _frame;
        }

        /// <summary>
        /// A tile that has not arrived, drawn from a coarser one that has.
        /// </summary>
        /// <remarks>
        /// Blurry for a moment and then sharp, which is how every map does it, and far better
        /// than a grey square where the country should be.
        /// </remarks>
        private void DrawWhatWeHave(DrawingContext context, Tile tile, Rect at)
        {
            for (var up = 1; up <= 5 && tile.Zoom - up >= 0; up++)
            {
                var step = 1 << up;
                var ancestor = new Tile(tile.Zoom - up, tile.X / step, tile.Y / step);
                if (!loaded.TryGetValue(ancestor.Key, out var bitmap))
                {
                    continue;
                }

                var part = (double)WebMercator.TileSize / step;
                var source = new Rect((tile.X % step) * part, (tile.Y % step) * part, part, part);
                context.DrawImage(bitmap, source, at);
                return;
            }
        }
    }
}

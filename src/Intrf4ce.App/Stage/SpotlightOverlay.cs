using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Intrf4ce.App.Stage;

/// <summary>
/// Dims the stage except for the focused part, in screen space, following the camera.
/// </summary>
/// <remarks>
/// Holes are kept in world coordinates and projected through the camera on every frame, so
/// the spotlight stays glued to its target while the camera is still flying towards it.
/// </remarks>
public sealed class SpotlightOverlay : Control
{
    private static readonly TimeSpan Fade = TimeSpan.FromMilliseconds(380);

    private readonly Skin _skin;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private IReadOnlyList<Rect> _holes = [];
    private Matrix _camera = Matrix.Identity;
    private double _strength;
    private double _from;
    private double _to;
    private TimeSpan _fadeStart;
    private bool _frameRequested;

    public SpotlightOverlay(Skin skin)
    {
        _skin = skin;
        IsHitTestVisible = false;
    }

    /// <summary>Lights these world rectangles and dims the rest; empty turns the spotlight off.</summary>
    public void Set(IReadOnlyList<Rect> worldHoles)
    {
        if (worldHoles.Count > 0)
        {
            _holes = worldHoles;
        }

        StartFade(worldHoles.Count > 0 ? 1 : 0);
    }

    public void Follow(Matrix camera)
    {
        _camera = camera;
        if (_strength > 0)
        {
            InvalidateVisual();
        }
    }

    public override void Render(DrawingContext context)
    {
        if (_strength <= 0.001 || _holes.Count == 0)
        {
            return;
        }

        var screen = new Rect(Bounds.Size);
        var holes = new GeometryGroup();
        var projected = new List<Rect>();
        foreach (var hole in _holes)
        {
            var rect = hole.TransformToAABB(_camera).Inflate(10);
            projected.Add(rect);
            holes.Children.Add(new RectangleGeometry(rect, 16, 16));
        }

        var dim = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(screen), holes);
        context.DrawGeometry(new SolidColorBrush(Color.FromArgb((byte)(165 * _strength), 0x04, 0x06, 0x09)), null, dim);

        var ring = new Pen(new SolidColorBrush(_skin.Accent, 0.75 * _strength), 2);
        foreach (var rect in projected)
        {
            context.DrawRectangle(null, ring, rect, 16, 16);
        }
    }

    private void StartFade(double to)
    {
        _from = _strength;
        _to = to;
        _fadeStart = _clock.Elapsed;
        RequestFrame();
    }

    private void RequestFrame()
    {
        if (_frameRequested || TopLevel.GetTopLevel(this) is not { } top)
        {
            // Not attached yet: jump straight to the end state.
            if (!_frameRequested)
            {
                _strength = _to;
            }

            return;
        }

        _frameRequested = true;
        top.RequestAnimationFrame(_ =>
        {
            _frameRequested = false;
            var t = Math.Clamp((_clock.Elapsed - _fadeStart) / Fade, 0, 1);
            _strength = _from + ((_to - _from) * (1 - Math.Pow(1 - t, 3)));
            InvalidateVisual();

            if (t < 1)
            {
                RequestFrame();
            }
            else if (_to == 0)
            {
                _holes = [];
            }
        });
    }
}

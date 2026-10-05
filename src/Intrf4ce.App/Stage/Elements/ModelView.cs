using System.Diagnostics;
using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Intrf4ce.Core.Model;
using Intrf4ce.Core.Shapes;

namespace Intrf4ce.App.Stage.Elements;

/// <summary>
/// A 3D model on a slide: turned as the agent says, spinning if it should, turned by the user's
/// drag or J and K, and turned to face a part when the narration focuses it.
/// </summary>
/// <remarks>
/// <para>
/// Every change of the way it faces is a turn, never a jump: the agent's new rotation, a focused
/// part, J and K, r for its own view. A drag is the user's own hand and follows the pointer
/// exactly. Once the user has turned it, it stays as they left it — no spin — until r.
/// </para>
/// <para>
/// It draws a frame only while something moves, and a spin pauses while the model is off the
/// screen, so a deck with a turning model three slides away costs nothing.
/// </para>
/// </remarks>
public sealed class ModelView : ElementView, IStageDraggable
{
    private const float StepDegrees = 20;
    private const double TurnSeconds = 0.75;

    /// <summary>A three-quarter view, a little from above: how a model looks most like a thing when nothing says otherwise.</summary>
    private static readonly Quaternion ThreeQuarter = Turntable(-30, 18, 0);

    private readonly ModelCanvas _canvas;
    private readonly TextBlock _detail;
    private readonly DockPanel _layout;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private TextBlock? _caption;
    private Quaternion _home = ThreeQuarter;
    private Quaternion _orientation = ThreeQuarter;
    private (Quaternion From, Quaternion To, double Start)? _turn;
    private float _explode;
    private float _explodeTarget;
    private float _spin;
    private bool _held;
    private bool _loaded;
    private string? _focused;
    private bool _frameAsked;
    private double? _lastFrame;

    public ModelView(ElementContext context, ModelElement element)
        : base(context, element)
    {
        _canvas = new ModelCanvas(context.Skin);
        _detail = new TextBlock
        {
            IsVisible = false,
            FontSize = 20,
            Foreground = context.Skin.Brush(context.Skin.Text, 0.9),
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 8, 0, 0),
        };

        DockPanel.SetDock(_detail, Dock.Bottom);
        _layout = new DockPanel { LastChildFill = true, Children = { _detail, _canvas } };
        Children.Add(_layout);
    }

    public override IReadOnlyList<string> Parts => [.. _canvas.Mesh?.Parts.Select(p => p.Id) ?? []];

    /// <summary>
    /// Where a part will be once the model has turned to face it — the camera frames that, not
    /// where the part is as the turn begins — and never less than a third of the model, so the
    /// stage does not dive into a tendon.
    /// </summary>
    public override Rect? PartBounds(string part)
    {
        if (_canvas.Direction(part) is not { } direction || _canvas.PartBounds(part, Facing(direction)) is not { } bounds)
        {
            return null;
        }

        var least = Math.Min(_canvas.Bounds.Width, _canvas.Bounds.Height) / 3;
        var grown = bounds.Inflate(new Thickness(Math.Max(12, (least - bounds.Width) / 2), Math.Max(12, (least - bounds.Height) / 2)));
        return _canvas.TranslatePoint(grown.TopLeft, this) is { } at ? new Rect(at, grown.Size) : null;
    }

    public override string? PartAt(Point point) =>
        this.TranslatePoint(point, _canvas) is { } local ? _canvas.PartAt(local) : null;

    public override string Describe(string? part) =>
        part is not null && _canvas.Mesh?.Parts.FirstOrDefault(p => p.Id == part) is { } found
            ? found.Label ?? found.Id
            : Element.Caption ?? "this 3D model";

    /// <summary>Turns to face the part, lights it and dims the rest; null turns it back and lets it spin again.</summary>
    public override void Highlight(string? part)
    {
        _focused = part is not null && _canvas.Direction(part) is not null ? part : null;
        var focused = _canvas.Mesh?.Parts.FirstOrDefault(p => p.Id == _focused);

        _detail.Text = focused is null ? "" : focused.Detail is { } detail ? $"{focused.Label ?? focused.Id} — {detail}" : "";
        _detail.IsVisible = _detail.Text.Length > 0;

        TurnTo(_focused is not null && _canvas.Direction(_focused) is { } direction ? Facing(direction) : _held ? _orientation : _home);
    }

    /// <summary>J and K: a turn about its own upright, twenty degrees a step; it stops spinning, the user has it.</summary>
    public override bool MoveCursor(int steps)
    {
        if (_canvas.Mesh is null)
        {
            return false;
        }

        _held = true;
        var from = _turn?.To ?? _orientation;
        TurnTo(Quaternion.Concatenate(About(Vector3.UnitY, StepDegrees * steps), from));
        return true;
    }

    /// <summary>r: back to the model's own view, spinning again if it was.</summary>
    public override void ResetCursor()
    {
        _held = false;
        _focused = null;
        _detail.IsVisible = false;
        TurnTo(_home);
    }

    public void DragBy(Avalonia.Vector delta)
    {
        // The pointer's own movement turns it about the screen's axes: across turns, up and down tilts.
        _held = true;
        _turn = null;
        var turned = Quaternion.Concatenate(_orientation, About(Vector3.UnitY, (float)delta.X * 0.45f));
        _orientation = Quaternion.Normalize(Quaternion.Concatenate(turned, About(Vector3.UnitX, (float)delta.Y * 0.45f)));
        Pose();
    }

    public void DragEnded()
    {
    }

    /// <summary>[x, y, z] degrees as a turntable: round about y, then tilted about x towards the viewer, then z.</summary>
    public static Quaternion Turntable(double y, double x, double z) =>
        Quaternion.Concatenate(Quaternion.Concatenate(About(Vector3.UnitY, (float)y), About(Vector3.UnitX, (float)x)), About(Vector3.UnitZ, (float)z));

    protected override async Task LoadAsync(Element element, CancellationToken cancellationToken)
    {
        var model = (ModelElement)element;

        IReadOnlyList<FileGroup>? file = null;
        if (model.Src is { } src)
        {
            var name = Uri.TryCreate(src, UriKind.Absolute, out var uri) && uri.Scheme.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? uri.AbsolutePath
                : src;
            var path = await Context.Images.ResolveFileAsync(src, Path.GetExtension(name).ToLowerInvariant(), cancellationToken);
            file = await Task.Run(() => ModelFiles.Read(path, Path.GetFileNameWithoutExtension(name)), cancellationToken);
        }

        var mesh = await Task.Run(() => MeshBuilder.Build(MeshBuilder.Parts(model, file)), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        var home = model.Rotation is [var x, var y, var z] ? Turntable(y, x, z) : ThreeQuarter;
        _canvas.Show(mesh, model.Style ?? "wire", model.Labels ?? true, home);
        _explodeTarget = (float)Math.Clamp(model.Explode ?? 0, 0, 1);
        _spin = (float)(model.Spin ?? 0);

        if (!_loaded)
        {
            (_home, _orientation, _explode, _loaded) = (home, home, _explodeTarget, true);
        }
        else if (home != _home)
        {
            // The agent turned it: a turn there, unless the user is holding it or a part is in focus.
            _home = home;
            if (!_held && _focused is null)
            {
                TurnTo(home);
            }
        }

        if (_caption is not null)
        {
            _layout.Children.Remove(_caption);
        }

        _caption = CaptionFor(model, Skin);
        if (_caption is not null)
        {
            DockPanel.SetDock(_caption, Dock.Bottom);
            _layout.Children.Insert(0, _caption);
        }

        Pose();
        Animate();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Animate();
    }

    /// <summary>The way that faces a part: turned round to it, and tilted towards it, but never so far the model stands on its head.</summary>
    private static Quaternion Facing(Vector3 direction)
    {
        if (direction.LengthSquared() < 1e-10f)
        {
            return ThreeQuarter;
        }

        var d = Vector3.Normalize(direction);
        var best = (Score: float.MinValue, Turn: ThreeQuarter);

        // Signs tried rather than derived: the one that brings the part nearest the viewer wins.
        foreach (var yawSign in new[] { 1f, -1f })
        {
            foreach (var pitchSign in new[] { 1f, -1f })
            {
                var yaw = yawSign * MathF.Atan2(d.X, d.Z);
                var round = Vector3.Transform(d, Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw));
                var pitch = pitchSign * Math.Clamp(MathF.Atan2(round.Y, MathF.Max(1e-4f, MathF.Abs(round.Z))), -0.7f, 0.7f);
                var turn = Quaternion.Concatenate(
                    Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw),
                    Quaternion.CreateFromAxisAngle(Vector3.UnitX, pitch + 0.12f));
                var seen = Vector3.Transform(d, turn);
                var score = seen.Z - (MathF.Abs(seen.X) * 2) - (MathF.Abs(seen.Y) * 0.5f);
                if (score > best.Score)
                {
                    best = (score, turn);
                }
            }
        }

        return best.Turn;
    }

    private static Quaternion About(Vector3 axis, float degrees) => Quaternion.CreateFromAxisAngle(axis, degrees * MathF.PI / 180);

    private void TurnTo(Quaternion target)
    {
        _turn = (_orientation, Quaternion.Normalize(target), _clock.Elapsed.TotalSeconds);
        Animate();
    }

    private void Pose() => _canvas.Pose = new ModelPose(_orientation, _explode, _focused);

    /// <summary>Asks for a frame, once; the frame asks for the next while anything is still moving.</summary>
    private void Animate()
    {
        if (_frameAsked || TopLevel.GetTopLevel(this) is not { } top)
        {
            return;
        }

        _frameAsked = true;
        top.RequestAnimationFrame(_ => Frame());
    }

    private void Frame()
    {
        _frameAsked = false;
        var now = _clock.Elapsed.TotalSeconds;
        var elapsed = _lastFrame is { } last ? (float)Math.Min(0.1, now - last) : 0f;
        var moving = false;

        if (_turn is { } turn)
        {
            var t = Math.Clamp((now - turn.Start) / TurnSeconds, 0, 1);
            var eased = (float)(t < 0.5 ? 4 * t * t * t : 1 - (Math.Pow((-2 * t) + 2, 3) / 2));
            _orientation = Quaternion.Normalize(Quaternion.Slerp(turn.From, turn.To, eased));
            if (t >= 1)
            {
                _turn = null;
            }

            moving = true;
        }
        else if (_spin != 0 && !_held && _focused is null)
        {
            if (OnScreen())
            {
                // Round its own upright, the way a turntable turns.
                _orientation = Quaternion.Normalize(Quaternion.Concatenate(About(Vector3.UnitY, _spin * elapsed), _orientation));
                moving = true;
            }
            else
            {
                // Off the stage: look again in a while rather than turn for nobody.
                DispatcherTimer.RunOnce(Animate, TimeSpan.FromMilliseconds(500));
            }
        }

        if (MathF.Abs(_explode - _explodeTarget) > 0.001f)
        {
            var step = 1.6f * elapsed;
            _explode = _explode < _explodeTarget ? MathF.Min(_explodeTarget, _explode + step) : MathF.Max(_explodeTarget, _explode - step);
            moving = true;
        }

        Pose();
        _lastFrame = moving ? now : null;
        if (moving)
        {
            Animate();
        }
    }

    /// <summary>Whether any of it is inside the window: a slide away, it is not.</summary>
    private bool OnScreen()
    {
        if (TopLevel.GetTopLevel(this) is not { } top || this.TransformToVisual(top) is not { } transform)
        {
            return false;
        }

        var drawn = new Rect(Bounds.Size).TransformToAABB(transform);
        return drawn.Width > 4 && drawn.Intersects(new Rect(top.ClientSize));
    }
}

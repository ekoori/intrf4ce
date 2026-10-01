using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace Intrf4ce.App.Hud;

public enum OrbMood
{
    Absent,

    Idle,

    Working,

    Error,
}

/// <summary>
/// The agent's mark at the head of the conversation: a round button like the menu's, still,
/// until the agent is working — then it spins.
/// </summary>
/// <remarks>
/// It used to glow and breathe all the time, which made "the agent is thinking" look exactly
/// like "the agent is there". Movement now means one thing only. At rest it wears the same
/// colours as the menu icon on the other side of the window, because it is the same kind of
/// thing: the handle that folds a column away.
/// </remarks>
public sealed class AgentOrb : Control
{
    private readonly Skin _skin;
    private readonly DispatcherTimer _timer;
    private readonly DateTime _start = DateTime.UtcNow;
    private OrbMood _mood = OrbMood.Absent;
    private bool _over;

    public AgentOrb(Skin skin)
    {
        _skin = skin;
        Width = Metrics.Button;
        Height = Metrics.Button;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, (_, _) => InvalidateVisual());

        PointerEntered += (_, _) =>
        {
            _over = true;
            InvalidateVisual();
        };

        PointerExited += (_, _) =>
        {
            _over = false;
            InvalidateVisual();
        };
    }

    public OrbMood Mood
    {
        get => _mood;
        set
        {
            if (_mood == value)
            {
                return;
            }

            _mood = value;
            if (value == OrbMood.Working && IsAttachedToVisualTree())
            {
                _timer.Start();
            }
            else
            {
                _timer.Stop();
            }

            InvalidateVisual();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_mood == OrbMood.Working)
        {
            _timer.Start();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _timer.Stop();
    }

    public override void Render(DrawingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var size = Math.Min(Bounds.Width, Bounds.Height);
        var centre = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var radius = (size / 2) - 0.5;

        // The same disc as every round button: Surface2, a hairline of Line, brighter under the pointer.
        context.DrawEllipse(_skin.Brush(_skin.Surface2, _over ? 1 : 0.85), new Pen(_skin.Brush(_skin.Line), 1), centre, radius, radius);

        // The agent itself: a dot in the colour the menu icon's lines are drawn in.
        var working = _mood == OrbMood.Working;
        var dot = size * 0.13;
        context.DrawEllipse(_skin.Brush(working ? _skin.Cool : _skin.Text, 0.95), null, centre, dot, dot);

        if (!working)
        {
            return;
        }

        // Working: three quarters of a ring, going round once a second.
        var t = (DateTime.UtcNow - _start).TotalSeconds;
        var ring = size * 0.3;
        var from = t * 2 * Math.PI;
        var to = from + (1.5 * Math.PI);
        var arc = new StreamGeometry();
        using (var draw = arc.Open())
        {
            draw.BeginFigure(new Point(centre.X + (ring * Math.Cos(from)), centre.Y + (ring * Math.Sin(from))), isFilled: false);
            draw.ArcTo(
                new Point(centre.X + (ring * Math.Cos(to)), centre.Y + (ring * Math.Sin(to))),
                new Size(ring, ring),
                rotationAngle: 0,
                isLargeArc: true,
                SweepDirection.Clockwise);
            draw.EndFigure(isClosed: false);
        }

        context.DrawGeometry(null, new Pen(_skin.Brush(_skin.Cool), 2.2, lineCap: PenLineCap.Round), arc);
    }

    private bool IsAttachedToVisualTree() => TopLevel.GetTopLevel(this) is not null;
}

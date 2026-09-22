using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace Intrf4ce.App.Hud;

public enum OrbMood
{
    /// <summary>No agent connected.</summary>
    Absent,

    /// <summary>Connected and waiting: a slow breath.</summary>
    Idle,

    /// <summary>Working on something: particles circling.</summary>
    Working,

    /// <summary>The narration is playing: it pulses with the voice.</summary>
    Speaking,

    Error,
}

/// <summary>
/// The agent's presence: a small glowing sphere that breathes, orbits or speaks.
/// </summary>
/// <remarks>
/// Knowledge Navigator gave its agent a face; a face overpromises. A single orb says as much
/// as the user needs at a glance (is anyone there, are they busy, is something wrong) and
/// costs nothing to ignore.
/// </remarks>
public sealed class AgentOrb : Control
{
    private readonly Skin _theme;
    private readonly DispatcherTimer _timer;
    private readonly DateTime _start = DateTime.UtcNow;
    private OrbMood _mood = OrbMood.Absent;

    public AgentOrb(Skin theme)
    {
        _theme = theme;
        Width = 30;
        Height = 30;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render, (_, _) => InvalidateVisual());
    }

    /// <summary>0 to 1: how loud the voice is right now, for the speaking pulse.</summary>
    public double Level { get; set; }

    public OrbMood Mood
    {
        get => _mood;
        set
        {
            _mood = value;
            InvalidateVisual();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _timer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _timer.Stop();
    }

    public override void Render(DrawingContext context)
    {
        var t = (DateTime.UtcNow - _start).TotalSeconds;
        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var radius = Math.Min(Bounds.Width, Bounds.Height) / 2 - 3;

        var color = _mood switch
        {
            OrbMood.Error => _theme.Danger,
            OrbMood.Absent => _theme.Faint,
            OrbMood.Speaking => _theme.Accent,
            _ => _theme.Cool,
        };

        var breath = _mood switch
        {
            OrbMood.Idle => 0.5 + (0.5 * Math.Sin(t * 1.6)),
            OrbMood.Speaking => Math.Clamp(Level, 0, 1),
            OrbMood.Working => 0.6 + (0.4 * Math.Sin(t * 5)),
            _ => 0.2,
        };

        // Glow, then core.
        var glow = new RadialGradientBrush
        {
            GradientStops =
            {
                new GradientStop(Color.FromArgb((byte)(90 + (110 * breath)), color.R, color.G, color.B), 0),
                new GradientStop(Color.FromArgb(0, color.R, color.G, color.B), 1),
            },
        };

        context.DrawEllipse(glow, null, center, radius + 3 + (3 * breath), radius + 3 + (3 * breath));

        var core = new RadialGradientBrush
        {
            GradientOrigin = new RelativePoint(0.35, 0.3, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Colors.White, 0),
                new GradientStop(color, 0.45),
                new GradientStop(Color.FromArgb(255, (byte)(color.R * 0.35), (byte)(color.G * 0.35), (byte)(color.B * 0.35)), 1),
            },
        };

        var coreRadius = radius * (0.62 + (0.08 * breath));
        context.DrawEllipse(core, null, center, coreRadius, coreRadius);

        if (_mood == OrbMood.Working)
        {
            var dot = new SolidColorBrush(color);
            for (var i = 0; i < 3; i++)
            {
                var angle = (t * 3.2) + (i * Math.PI * 2 / 3);
                var p = new Point(center.X + (Math.Cos(angle) * radius), center.Y + (Math.Sin(angle) * radius));
                context.DrawEllipse(dot, null, p, 2.2, 2.2);
            }
        }
    }
}

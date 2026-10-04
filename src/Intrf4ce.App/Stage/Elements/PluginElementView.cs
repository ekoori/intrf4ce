using Avalonia;
using Avalonia.Controls;
using Intrf4ce.Core.Model;
using Intrf4ce.Plugins;

namespace Intrf4ce.App.Stage.Elements;

/// <summary>
/// An element a plugin draws: the plugin's live element inside, and around it what every
/// element has — the caption, the error card, the parts focus cues aim at.
/// </summary>
/// <remarks>
/// The live element is made once and then told each new version of the element's fields, so a
/// change the agent makes is a change, not a rebuild: a clock does not flicker because a
/// zone was added. It is disposed when the view leaves the stage, which is how its own clock
/// stops, and made again if the view comes back.
/// </remarks>
public sealed class PluginElementView : ElementView
{
    private readonly DockPanel _layout = new() { LastChildFill = true };
    private LiveElement? _live;
    private TextBlock? _caption;

    public PluginElementView(ElementContext context, PluginElement element)
        : base(context, element)
    {
        Children.Add(_layout);
    }

    /// <summary>Whether a plugin is drawing it, rather than a card saying none can.</summary>
    public bool IsLive => _live is not null;

    public override IReadOnlyList<string> Parts => _live?.Parts ?? [];

    /// <summary>What it shows now, in the plugin's words.</summary>
    public string Reading => _live?.Describe() is { Length: > 0 } words ? words : Describe(null);

    public override Rect? PartBounds(string part) =>
        _live?.PartBounds(part) is { } bounds && _live.View.TranslatePoint(bounds.TopLeft, this) is { } at
            ? new Rect(at, bounds.Size)
            : null;

    public override void Highlight(string? part) => _live?.Highlight(part);

    protected override async Task LoadAsync(Element element, CancellationToken cancellationToken)
    {
        var plugin = (PluginElement)element;

        if (_live is null)
        {
            _live = Context.Plugins?.Create(plugin.Kind, SlideId, plugin.Id);
            if (_live is null)
            {
                Fail($"a {plugin.Kind} needs the plugin that draws it, and none that is loaded does. :plugins lists the ones that are.");
                return;
            }

            _layout.Children.Clear();
            _caption = CaptionFor(plugin, Skin);
            if (_caption is not null)
            {
                DockPanel.SetDock(_caption, Dock.Bottom);
                _layout.Children.Add(_caption);
            }

            _layout.Children.Add(_live.View);
        }
        else if (_caption is not null)
        {
            _caption.Text = plugin.Caption;
        }

        await _live.ShowAsync(plugin.Fields, cancellationToken);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        // Back on the stage after being taken off it: the plugin starts over.
        if (_live is null && _layout.Children.Count > 0)
        {
            _layout.Children.Clear();
            Update(Element, force: true);
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Cancel();

        if (_live is not null)
        {
            try
            {
                _live.Dispose();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log.Error($"disposing the {Element.TypeName} '{Element.Id}'", ex);
            }

            _live = null;
        }
    }
}

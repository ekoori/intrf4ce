using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Intrf4ce.App.Media;
using Intrf4ce.Core.Model;
using Intrf4ce.Media.Images;
using Intrf4ce.Core.Rendering;

namespace Intrf4ce.App.Stage.Elements;

/// <summary>What every element view needs from the rest of the app.</summary>
/// <param name="Publish">Tells the agent something the user did (type, text, routing details).</param>
public sealed record ElementContext(
    Skin Skin,
    IRenderService Renderer,
    ImageLoader Images,
    TileSource Tiles,
    Action<string, string, IReadOnlyDictionary<string, string>> Publish);

/// <summary>
/// The on-stage view of one element: loads its content, reports whether that worked, and
/// knows where its parts are.
/// </summary>
/// <remarks>
/// Every load is asynchronous (downloads, decodes, renders) and may be overtaken by a newer
/// version of the element; each <see cref="Update"/> cancels the previous load, and
/// <see cref="Ready"/> always refers to the latest one. Failures are drawn in place as an
/// error card and reported as issues, never thrown: one bad diagram must not take the
/// slide, or the stage, down with it.
/// </remarks>
public abstract class ElementView : Panel
{
    private readonly List<SpecIssue> _issues = [];
    private TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenSource? _loading;
    private Control? _errorCard;

    protected ElementView(ElementContext context, Element element)
    {
        Context = context;
        Element = element;
        ClipToBounds = false;
    }

    protected ElementContext Context { get; }

    protected Skin Skin => Context.Skin;

    public Element Element { get; private set; }

    public string Id => Element.Id;

    /// <summary>The slide this element is on, for the events it reports.</summary>
    public string SlideId { get; set; } = "";

    public Task Ready => _ready.Task;

    public IReadOnlyList<SpecIssue> Issues => _issues;

    /// <summary>Ids that can follow the dot in a focus cue, once known.</summary>
    public virtual IReadOnlyList<string> Parts => [];

    /// <summary>Where a part is, in this view's coordinates; null if there is no such part.</summary>
    public virtual Rect? PartBounds(string part) => null;

    /// <summary>Which part is under <paramref name="point"/> (this view's coordinates), for clicks; null if none.</summary>
    public virtual string? PartAt(Point point) =>
        Parts.FirstOrDefault(p => PartBounds(p) is { } bounds && bounds.Contains(point));

    /// <summary>Marks a part as the one in focus, beyond the spotlight the stage draws; null clears it.</summary>
    public virtual void Highlight(string? part)
    {
    }

    /// <summary>Where else a focused part shows itself (a callout's label), for the spotlight to light as well.</summary>
    public virtual Rect? LabelBounds(string part) => null;

    /// <summary>A few words naming a part, or the whole element when null, for "Ask about this".</summary>
    public virtual string Describe(string? part) => part ?? Element.Caption ?? $"this {Element.TypeName}";

    /// <summary>
    /// A zoom region, given as fractions, in this view's coordinates. Fractions of the whole
    /// element by default; pictures override it to mean fractions of the picture, because that
    /// is what the agent measured them on.
    /// </summary>
    public virtual Rect RegionBounds(double x, double y, double width, double height) =>
        new(x * Bounds.Width, y * Bounds.Height, width * Bounds.Width, height * Bounds.Height);

    /// <summary>Starts (or restarts) loading this element. Called once on creation and on every change.</summary>
    public void Update(Element element, bool force = false)
    {
        if (!force && ReferenceEquals(element, Element) && _loading is not null)
        {
            return;
        }

        Element = element;
        _loading?.Cancel();
        _loading = new CancellationTokenSource();
        _issues.Clear();
        ClearError();

        var previous = _ready;
        _ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        previous.TrySetResult();

        _ = RunLoadAsync(element, _ready, _loading.Token);
    }

    public void Cancel() => _loading?.Cancel();

    protected abstract Task LoadAsync(Element element, CancellationToken cancellationToken);

    protected void Warn(string message) =>
        _issues.Add(new SpecIssue(IssueSeverity.Warning, $"element '{Id}'", message));

    protected void Fail(string message)
    {
        _issues.Add(new SpecIssue(IssueSeverity.Warning, $"element '{Id}'", message));
        ShowError(message);
    }

    private async Task RunLoadAsync(Element element, TaskCompletionSource ready, CancellationToken cancellationToken)
    {
        try
        {
            await LoadAsync(element, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Log.Error($"loading {element.TypeName} '{element.Id}'", ex);
            Fail($"could not be shown: {ex.Message}");
        }
        finally
        {
            ready.TrySetResult();
        }
    }

    private void ShowError(string message)
    {
        ClearError();
        _errorCard = new Border
        {
            Background = Skin.Brush(Skin.Danger, 0.12),
            BorderBrush = Skin.Brush(Skin.Danger, 0.6),
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(24),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Child = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock
                    {
                        Text = $"{Element.TypeName} “{Element.Id}”",
                        Foreground = Skin.Brush(Skin.Danger),
                        FontSize = 22,
                        FontWeight = FontWeight.SemiBold,
                    },
                    new TextBlock
                    {
                        Text = message,
                        Foreground = Skin.Brush(Skin.Text, 0.85),
                        FontSize = 19,
                        TextWrapping = TextWrapping.Wrap,
                        MaxLines = 8,
                        FontFamily = new FontFamily("Cascadia Mono, Consolas, Menlo, monospace"),
                    },
                },
            },
        };

        Children.Add(_errorCard);
    }

    private void ClearError()
    {
        if (_errorCard is not null)
        {
            Children.Remove(_errorCard);
            _errorCard = null;
        }
    }

    /// <summary>A caption line under the content, when the element has one.</summary>
    protected static TextBlock? CaptionFor(Element element, Skin theme) =>
        string.IsNullOrWhiteSpace(element.Caption)
            ? null
            : new TextBlock
            {
                Text = element.Caption,
                Foreground = theme.Brush(theme.Muted),
                FontSize = 18,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 10, 0, 0),
            };
}

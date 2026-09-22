using Intrf4ce.Core.Model;

namespace Intrf4ce.App.Stage.Elements;

/// <summary>
/// The one place that knows which view draws which element type.
/// </summary>
/// <remarks>
/// A test walks every type in <see cref="Element"/>'s polymorphism list through this method,
/// so a new element type cannot reach the agent's schema without a view (or at least a
/// placeholder that says it is missing).
/// </remarks>
public static class ElementViews
{
    public static ElementView Create(ElementContext context, Element element) => element switch
    {
        TitleElement title => new TitleView(context, title),
        MarkdownElement markdown => new MarkdownView(context, markdown),
        ImageElement image => new ImageView(context, image),
        DiagramElement or MathElement or ChartElement => new SvgElementView(context, element),
        _ => new PlaceholderView(context, element),
    };

    /// <summary>Whether an existing view can take a new version of the element, or must be replaced.</summary>
    public static bool CanReuse(ElementView view, Element element) =>
        view.Element.GetType() == element.GetType();
}

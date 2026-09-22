using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Intrf4ce.Core.Model;

namespace Intrf4ce.App.Stage.Elements;

/// <summary>Stands in for an element type whose view has not been written yet, and says so.</summary>
public sealed class PlaceholderView : ElementView
{
    public PlaceholderView(ElementContext context, Element element)
        : base(context, element)
    {
        Children.Add(new Border
        {
            BorderBrush = Skin.Brush(Skin.Line),
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(14),
            Background = Skin.Brush(Skin.Surface2, 0.5),
            Child = new TextBlock
            {
                Text = $"{element.TypeName} “{element.Id}”",
                Foreground = Skin.Brush(Skin.Muted),
                FontSize = 24,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        });
    }

    protected override Task LoadAsync(Element element, CancellationToken cancellationToken)
    {
        Warn($"{element.TypeName} elements are not drawn yet in this build; it is shown as a placeholder");
        return Task.CompletedTask;
    }
}

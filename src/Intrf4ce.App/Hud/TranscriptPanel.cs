using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;

namespace Intrf4ce.App.Hud;

/// <summary>
/// Everything said in the chat since the app started, in a panel on the right (T).
/// </summary>
/// <remarks>
/// The chat bar lets messages fade so they never compete with the stage; this is where they
/// can be read again.
/// </remarks>
public sealed class TranscriptPanel : Border
{
    private readonly Skin _skin;
    private readonly StackPanel _list = new() { Spacing = 14 };
    private readonly ScrollViewer _scroller;
    private readonly TextBlock _empty;

    public TranscriptPanel(Skin skin)
    {
        _skin = skin;

        HorizontalAlignment = HorizontalAlignment.Right;
        VerticalAlignment = VerticalAlignment.Stretch;
        Width = 460;
        Margin = new Thickness(0, 64, 20, 118);
        Background = new SolidColorBrush(Color.FromArgb(242, 0x10, 0x15, 0x1C));
        BorderBrush = skin.Brush(skin.Line);
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(16);
        BoxShadow = new BoxShadows(new BoxShadow { Blur = 30, Color = Color.FromArgb(160, 0, 0, 0), OffsetY = 8 });
        IsVisible = false;

        _empty = new TextBlock
        {
            Text = "Nothing has been said yet.",
            Foreground = skin.Brush(skin.Faint),
            FontSize = 15,
        };
        _list.Children.Add(_empty);

        _scroller = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(20, 4, 20, 18),
            Content = _list,
        };

        var title = new TextBlock
        {
            Text = "Conversation",
            FontSize = 17,
            FontWeight = FontWeight.SemiBold,
            Foreground = skin.Brush(skin.Text),
        };

        var hint = new TextBlock
        {
            Text = "T to close",
            FontSize = 13,
            Foreground = skin.Brush(skin.Faint),
            VerticalAlignment = VerticalAlignment.Center,
        };

        var header = new DockPanel { Margin = new Thickness(20, 16, 20, 12) };
        DockPanel.SetDock(hint, Dock.Right);
        header.Children.Add(hint);
        header.Children.Add(title);

        var layout = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        layout.Children.Add(header);
        layout.Children.Add(_scroller);
        Child = layout;
    }

    public void Add(ChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        _list.Children.Remove(_empty);
        var who = new TextBlock
        {
            Text = $"{(message.FromUser ? "You" : "Agent")} · {message.At:HH:mm}",
            FontSize = 12,
            Foreground = _skin.Brush(message.IsError ? _skin.Danger : message.FromUser ? _skin.Muted : _skin.Cool),
        };

        var text = new SelectableTextBlock
        {
            Text = message.Text,
            FontSize = 15,
            LineHeight = 22,
            TextWrapping = TextWrapping.Wrap,
            Foreground = _skin.Brush(_skin.Text, message.FromUser ? 0.8 : 0.95),
        };

        _list.Children.Add(new StackPanel { Spacing = 3, Children = { who, text } });
        if (IsVisible)
        {
            _scroller.ScrollToEnd();
        }
    }

    public void Toggle()
    {
        IsVisible = !IsVisible;
        if (IsVisible)
        {
            _scroller.ScrollToEnd();
        }
    }
}

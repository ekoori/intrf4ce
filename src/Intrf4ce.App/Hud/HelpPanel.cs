using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Intrf4ce.Core.Commands;
using Intrf4ce.Core.Input;

namespace Intrf4ce.App.Hud;

/// <summary>
/// Every command, grouped, with the keys each one is on — built from the commands themselves,
/// so it cannot describe one that does not exist or miss one that does.
/// </summary>
public sealed class HelpPanel : Border
{
    private static readonly FontFamily Mono = new("Cascadia Mono, Consolas, Menlo, monospace");

    private readonly Skin _skin;
    private readonly StackPanel _rows = new() { Spacing = 2 };

    public HelpPanel(Skin skin)
    {
        ArgumentNullException.ThrowIfNull(skin);
        _skin = skin;

        IsVisible = false;
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Center;
        Width = 760;
        MaxHeight = 760;
        CornerRadius = new CornerRadius(18);
        Background = new SolidColorBrush(Color.FromArgb(250, 0x11, 0x17, 0x1F));
        BorderBrush = skin.Brush(skin.Line);
        BorderThickness = new Thickness(1);
        BoxShadow = BoxShadows.Parse("0 24 60 0 #B0000000");

        var title = new TextBlock
        {
            Text = "Keys and commands",
            FontSize = 20,
            FontWeight = FontWeight.SemiBold,
            Foreground = skin.Brush(skin.Text),
            VerticalAlignment = VerticalAlignment.Center,
        };

        var note = new TextBlock
        {
            Text = "Every one can be typed after : and put on keys with :bind, or in keys: in settings.yaml. A number before a key repeats it: 3l.",
            FontSize = 12,
            Foreground = skin.Brush(skin.Faint),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0),
        };

        var close = new IconButton(skin, Glyph.Close, "Close (Esc)", () => IsVisible = false, size: 32);
        var header = new DockPanel { Margin = new Thickness(22, 18, 18, 12) };
        DockPanel.SetDock(close, Dock.Right);
        header.Children.Add(close);
        header.Children.Add(new StackPanel { Children = { title, note } });

        var scroller = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = new StackPanel { Margin = new Thickness(22, 0, 22, 18), Children = { _rows } },
        };

        var layout = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        layout.Children.Add(header);
        layout.Children.Add(scroller);
        Child = layout;
    }

    public void Show(IReadOnlyList<CommandInfo> commands, KeyMap keys)
    {
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(keys);

        _rows.Children.Clear();

        // In the order a person meets them, not the alphabet's.
        string[] order = ["Moving around", "The performance", "The window", "Sessions", "The agent", "Settings and keys"];
        foreach (var group in commands.GroupBy(c => c.Group).OrderBy(g => Array.IndexOf(order, g.Key) is var i and >= 0 ? i : order.Length))
        {
            _rows.Children.Add(new TextBlock
            {
                Text = group.Key,
                FontSize = 12,
                FontWeight = FontWeight.SemiBold,
                Foreground = _skin.Brush(_skin.Cool, 0.9),
                Margin = new Thickness(0, 14, 0, 6),
            });

            foreach (var command in group.OrderBy(c => c.Name, StringComparer.Ordinal))
            {
                _rows.Children.Add(Row(command, keys.KeysFor(command.Name)));
            }
        }

        IsVisible = true;
    }

    private Grid Row(CommandInfo command, IReadOnlyList<string> keys)
    {
        var bound = new TextBlock
        {
            Text = string.Join("  ", keys),
            FontFamily = Mono,
            FontSize = 13,
            Foreground = _skin.Brush(_skin.Accent),
            VerticalAlignment = VerticalAlignment.Top,
            TextWrapping = TextWrapping.Wrap,
        };

        var usage = new TextBlock
        {
            Text = command.Usage,
            FontFamily = Mono,
            FontSize = 13,
            Foreground = _skin.Brush(_skin.Text, 0.95),
            VerticalAlignment = VerticalAlignment.Top,
            TextWrapping = TextWrapping.Wrap,
        };

        var about = new TextBlock
        {
            Text = command.Description + (command.Aliases.Count > 0 ? $"  (also :{string.Join(", :", command.Aliases)})" : ""),
            FontSize = 13,
            Foreground = _skin.Brush(_skin.Muted),
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Top,
        };

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("130,230,*"), Margin = new Thickness(0, 3) };
        Grid.SetColumn(bound, 0);
        Grid.SetColumn(usage, 1);
        Grid.SetColumn(about, 2);
        row.Children.Add(bound);
        row.Children.Add(usage);
        row.Children.Add(about);
        return row;
    }
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;

namespace Intrf4ce.App.Hud;

/// <summary>Files dropped for the agent: what they are, and how to show one small.</summary>
public static class Attachment
{
    private static readonly HashSet<string> Pictures = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".tif", ".tiff", ".heic", ".svg",
    };

    /// <summary>image, folder or file — how the agent is told what it has been given.</summary>
    public static string Kind(string path) =>
        Directory.Exists(path) ? "folder" : Pictures.Contains(Path.GetExtension(path)) ? "image" : "file";

    /// <summary>A small picture of an image, or null for anything that is not one or will not decode.</summary>
    public static Bitmap? Thumbnail(string path, int width)
    {
        if (Kind(path) != "image" || Path.GetExtension(path).Equals(".svg", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            using var stream = File.OpenRead(path);
            return Bitmap.DecodeToWidth(stream, width);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException
            or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Makes <paramref name="target"/> take dropped files, showing <paramref name="overlay"/>
    /// while something droppable is over it.
    /// </summary>
    public static void AcceptDrops(Control target, Control? overlay, Action<IReadOnlyList<string>> dropped)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(dropped);

        DragDrop.SetAllowDrop(target, true);

        DragDrop.AddDragOverHandler(target, (_, e) =>
        {
            var files = e.DataTransfer.Contains(DataFormat.File);
            e.DragEffects = files ? DragDropEffects.Copy : DragDropEffects.None;
            if (overlay is not null)
            {
                overlay.IsVisible = files;
            }

            e.Handled = true;
        });

        DragDrop.AddDragLeaveHandler(target, (_, _) =>
        {
            if (overlay is not null)
            {
                overlay.IsVisible = false;
            }
        });

        DragDrop.AddDropHandler(target, (_, e) =>
        {
            if (overlay is not null)
            {
                overlay.IsVisible = false;
            }

            var paths = (e.DataTransfer.TryGetFiles() ?? [])
                .Select(f => f.TryGetLocalPath())
                .OfType<string>()
                .Where(p => p.Length > 0)
                .ToList();

            if (paths.Count > 0)
            {
                dropped(paths);
                e.Handled = true;
            }
        });
    }

    /// <summary>The veil shown over a drop target while files are held over it.</summary>
    public static Border Overlay(Skin skin, string words)
    {
        ArgumentNullException.ThrowIfNull(skin);

        return new Border
        {
            IsVisible = false,
            IsHitTestVisible = false,
            Background = skin.Brush(skin.Ink, 0.72),
            BorderBrush = skin.Brush(skin.Accent, 0.9),
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(16),
            Margin = new Thickness(10),
            Child = new TextBlock
            {
                Text = words,
                FontSize = 18,
                Foreground = skin.Brush(skin.Accent),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(20),
            },
        };
    }
}

/// <summary>
/// The files waiting to go with the next message: a small picture of each, its name, and a way
/// to take it back.
/// </summary>
public sealed class AttachmentStrip : WrapPanel
{
    private readonly Skin _skin;

    public AttachmentStrip(Skin skin)
    {
        ArgumentNullException.ThrowIfNull(skin);
        _skin = skin;
        Orientation = Orientation.Horizontal;
        IsVisible = false;
    }

    /// <summary>A file was taken back off the message.</summary>
    public event EventHandler<string>? Removed;

    public void Show(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        Children.Clear();
        foreach (var path in paths)
        {
            Children.Add(Chip(path));
        }

        IsVisible = paths.Count > 0;
    }

    private Border Chip(string path)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };

        if (Attachment.Thumbnail(path, 64) is { } picture)
        {
            row.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(4),
                ClipToBounds = true,
                Child = new Image { Source = picture, Width = 32, Height = 24, Stretch = Stretch.UniformToFill },
            });
        }

        row.Children.Add(new TextBlock
        {
            Text = Path.GetFileName(path.TrimEnd('\\', '/')) is { Length: > 0 } name ? name : path,
            FontSize = 12,
            MaxWidth = 150,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = _skin.Brush(_skin.Text, 0.9),
            VerticalAlignment = VerticalAlignment.Center,
        });

        var remove = new TextBlock
        {
            Text = "✕",
            FontSize = 11,
            Foreground = _skin.Brush(_skin.Muted),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand),
            Margin = new Thickness(2, 0, 0, 0),
        };

        remove.PointerPressed += (_, e) =>
        {
            Removed?.Invoke(this, path);
            e.Handled = true;
        };

        row.Children.Add(remove);

        var chip = new Border
        {
            Padding = new Thickness(6, 4, 8, 4),
            Margin = new Thickness(0, 0, 6, 6),
            CornerRadius = new CornerRadius(8),
            Background = _skin.Brush(_skin.Surface2, 0.9),
            BorderBrush = _skin.Brush(_skin.Line),
            BorderThickness = new Thickness(1),
            Child = row,
        };

        chip.SetValue(ToolTip.TipProperty, path);
        return chip;
    }
}

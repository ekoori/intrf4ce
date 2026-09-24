using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Intrf4ce.Core.Settings;

namespace Intrf4ce.App.Hud;

/// <summary>
/// The settings, on the stage, over whatever is playing.
/// </summary>
/// <remarks>
/// settings.yaml is still the file of record — it is the user's, comments and all, and the
/// agent writes to it through the same door — but a presentation is watched, not
/// administered, and nobody should have to find a YAML file to turn the subtitles off. Every
/// control here writes one line of that file and lets the watcher apply it, so the panel and
/// the file can never disagree.
/// <para>
/// Only the settings worth changing mid-presentation are here; the rest, where a wrong value
/// strands the app, stay in the file where changing them is a considered act. The button at
/// the bottom opens it.
/// </para>
/// </remarks>
public sealed class SettingsPanel : Border
{
    private readonly Skin _skin;
    private readonly StackPanel _rows = new() { Spacing = 2 };
    private readonly TextBlock _note;
    private readonly List<Action<AppSettings>> _refresh = [];
    private readonly string _file;

    public SettingsPanel(Skin skin, string settingsFile)
    {
        ArgumentNullException.ThrowIfNull(skin);
        _skin = skin;
        _file = settingsFile;

        IsVisible = false;
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Center;
        Width = 560;
        MaxHeight = 720;
        CornerRadius = new CornerRadius(18);
        Background = new SolidColorBrush(Color.FromArgb(250, 0x11, 0x17, 0x1F));
        BorderBrush = skin.Brush(skin.Line);
        BorderThickness = new Thickness(1);
        BoxShadow = BoxShadows.Parse("0 24 60 0 #B0000000");

        var title = new TextBlock
        {
            Text = "Settings",
            FontSize = 20,
            FontWeight = FontWeight.SemiBold,
            Foreground = skin.Brush(skin.Text),
            VerticalAlignment = VerticalAlignment.Center,
        };

        var close = new IconButton(skin, Glyph.Close, "Close (Esc)", () => Close?.Invoke(this, EventArgs.Empty), size: 32);

        var header = new DockPanel { Margin = new Thickness(22, 18, 18, 10) };
        DockPanel.SetDock(close, Dock.Right);
        header.Children.Add(close);
        header.Children.Add(title);

        _note = new TextBlock
        {
            FontSize = 12,
            Foreground = skin.Brush(skin.Faint),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(22, 0, 22, 10),
            Text = $"Saved to {settingsFile}. The agent can change these too.",
        };

        var open = Link("Open settings.yaml", () => Reveal(_file));

        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 14,
            Margin = new Thickness(22, 4, 22, 18),
            Children = { open },
        };

        var scroller = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = new StackPanel { Margin = new Thickness(14, 0, 14, 8), Children = { _rows } },
        };

        var layout = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(_note, Dock.Bottom);
        DockPanel.SetDock(footer, Dock.Bottom);
        layout.Children.Add(header);
        layout.Children.Add(footer);
        layout.Children.Add(_note);
        layout.Children.Add(scroller);
        Child = layout;

        Build();
    }

    /// <summary>A setting was changed: the key as it reads in the file, and its new value.</summary>
    public event EventHandler<(string Key, string Value)>? Changed;

    public event EventHandler? Close;

    /// <summary>Shows the panel with the values the app is running with.</summary>
    public void Show(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        foreach (var refresh in _refresh)
        {
            refresh(settings);
        }

        IsVisible = true;
    }

    /// <summary>Says why a change did not take, in place, rather than in the conversation.</summary>
    public void Complain(string message)
    {
        _note.Text = message;
        _note.Foreground = _skin.Brush(_skin.Danger);
    }

    private void Build()
    {
        string? section = null;
        foreach (var entry in SettingsSchema.All)
        {
            var part = entry.Key[..entry.Key.IndexOf('.', StringComparison.Ordinal)];
            if (part != section)
            {
                section = part;
                _rows.Children.Add(new TextBlock
                {
                    Text = part switch
                    {
                        "narration" => "Narration",
                        "stage" => "The stage",
                        "library" => "Sessions",
                        "agent" => "The agent",
                        _ => part,
                    },
                    FontSize = 12,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = _skin.Brush(_skin.Cool, 0.9),
                    Margin = new Thickness(8, 16, 0, 6),
                });
            }

            _rows.Children.Add(Row(entry));
        }
    }

    private Control Row(SettingEntry entry)
    {
        var label = new TextBlock
        {
            Text = entry.Label + (entry.NeedsRestart ? " ·" : ""),
            FontSize = 15,
            Foreground = _skin.Brush(_skin.Text, 0.95),
        };

        var description = new TextBlock
        {
            Text = entry.Description + (entry.NeedsRestart ? " Takes effect next time intrf4ce starts." : ""),
            FontSize = 12,
            Foreground = _skin.Brush(_skin.Faint),
            TextWrapping = TextWrapping.Wrap,
        };

        var control = entry.Kind switch
        {
            SettingKind.Toggle => Toggle(entry),
            SettingKind.Choice => Choice(entry),
            SettingKind.Number => Number(entry),
            _ => Text(entry),
        };

        var words = new StackPanel { Spacing = 2, Children = { label, description } };
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(8, 8, 8, 8) };
        Grid.SetColumn(words, 0);
        Grid.SetColumn(control, 1);
        control.VerticalAlignment = VerticalAlignment.Center;
        control.Margin = new Thickness(16, 0, 0, 0);
        row.Children.Add(words);
        row.Children.Add(control);

        return new Border
        {
            CornerRadius = new CornerRadius(12),
            Background = _skin.Brush(_skin.Surface, 0.5),
            Margin = new Thickness(0, 0, 0, 4),
            Child = row,
        };
    }

    private Control Toggle(SettingEntry entry)
    {
        var knob = new Border
        {
            Width = 18,
            Height = 18,
            CornerRadius = new CornerRadius(9),
            Background = _skin.Brush(_skin.Text),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(3, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };

        var track = new Border
        {
            Width = 46,
            Height = 24,
            CornerRadius = new CornerRadius(12),
            Background = _skin.Brush(_skin.Surface2),
            BorderBrush = _skin.Brush(_skin.Line),
            BorderThickness = new Thickness(1),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = knob,
        };

        var on = false;
        void Paint()
        {
            track.Background = on ? _skin.Brush(_skin.Accent, 0.9) : _skin.Brush(_skin.Surface2);
            knob.Background = on ? _skin.Brush(_skin.Ink) : _skin.Brush(_skin.Muted);
            knob.HorizontalAlignment = on ? HorizontalAlignment.Right : HorizontalAlignment.Left;
            knob.Margin = on ? new Thickness(0, 0, 3, 0) : new Thickness(3, 0, 0, 0);
        }

        track.PointerPressed += (_, e) =>
        {
            e.Handled = true;
            on = !on;
            Paint();
            Changed?.Invoke(this, (entry.Key, on ? "true" : "false"));
        };

        _refresh.Add(settings =>
        {
            on = SettingsSchema.Read(settings, entry.Key) == "true";
            Paint();
        });

        Paint();
        return track;
    }

    private Control Choice(SettingEntry entry)
    {
        var pills = new WrapPanel { Orientation = Orientation.Horizontal, MaxWidth = 250, HorizontalAlignment = HorizontalAlignment.Right };
        var buttons = new List<(string Value, Border Pill)>();

        foreach (var choice in entry.Choices!)
        {
            var pill = new Border
            {
                Padding = new Thickness(11, 5),
                Margin = new Thickness(4, 2, 0, 2),
                CornerRadius = new CornerRadius(9),
                BorderThickness = new Thickness(1),
                Cursor = new Cursor(StandardCursorType.Hand),
                Child = new TextBlock { Text = choice, FontSize = 13, Foreground = _skin.Brush(_skin.Text, 0.9) },
            };

            pill.PointerPressed += (_, e) =>
            {
                e.Handled = true;
                Changed?.Invoke(this, (entry.Key, choice));
            };

            buttons.Add((choice, pill));
            pills.Children.Add(pill);
        }

        _refresh.Add(settings =>
        {
            var value = SettingsSchema.Read(settings, entry.Key);
            foreach (var (choice, pill) in buttons)
            {
                var chosen = string.Equals(choice, value, StringComparison.OrdinalIgnoreCase);
                pill.Background = chosen ? _skin.Brush(_skin.Accent, 0.22) : _skin.Brush(_skin.Surface2, 0.7);
                pill.BorderBrush = chosen ? _skin.Brush(_skin.Accent, 0.9) : _skin.Brush(_skin.Line);
            }
        });

        return pills;
    }

    private Control Number(SettingEntry entry)
    {
        var value = new TextBlock
        {
            FontSize = 14,
            MinWidth = 38,
            TextAlignment = TextAlignment.Right,
            Foreground = _skin.Brush(_skin.Text, 0.9),
            VerticalAlignment = VerticalAlignment.Center,
        };

        var slider = new Slider
        {
            Width = 160,
            Minimum = entry.Minimum,
            Maximum = entry.Maximum,
            SmallChange = entry.Step,
            LargeChange = entry.Step * 2,
            TickFrequency = entry.Step,
            IsSnapToTickEnabled = true,
            VerticalAlignment = VerticalAlignment.Center,
        };

        // Fluent draws a slider in its own accent, which here is a magenta nobody asked for.
        foreach (var key in new[] { "SliderTrackValueFill", "SliderTrackValueFillPointerOver", "SliderTrackValueFillPressed", "SliderThumbBackground", "SliderThumbBackgroundPointerOver", "SliderThumbBackgroundPressed" })
        {
            slider.Resources[key] = _skin.Brush(_skin.Accent);
        }

        foreach (var key in new[] { "SliderTrackFill", "SliderTrackFillPointerOver", "SliderTrackFillPressed" })
        {
            slider.Resources[key] = _skin.Brush(_skin.Line);
        }

        var settling = false;
        slider.PropertyChanged += (_, e) =>
        {
            if (e.Property != RangeBase.ValueProperty)
            {
                return;
            }

            value.Text = slider.Value.ToString("0.##", CultureInfo.InvariantCulture);
            if (!settling)
            {
                Changed?.Invoke(this, (entry.Key, value.Text));
            }
        };

        _refresh.Add(settings =>
        {
            settling = true;
            slider.Value = double.TryParse(SettingsSchema.Read(settings, entry.Key), NumberStyles.Float, CultureInfo.InvariantCulture, out var current)
                ? current
                : entry.Minimum;
            value.Text = slider.Value.ToString("0.##", CultureInfo.InvariantCulture);
            settling = false;
        });

        return new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { slider, value } };
    }

    private Control Text(SettingEntry entry)
    {
        var box = new TextBox
        {
            Width = 190,
            FontSize = 14,
            Background = _skin.Brush(_skin.Surface2, 0.8),
            BorderBrush = _skin.Brush(_skin.Line),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9),
            Foreground = _skin.Brush(_skin.Text),
            Padding = new Thickness(9, 6),
            PlaceholderText = "default",
        };

        box.LostFocus += (_, _) => Changed?.Invoke(this, (entry.Key, box.Text ?? ""));
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Changed?.Invoke(this, (entry.Key, box.Text ?? ""));
                e.Handled = true;
            }
        };

        _refresh.Add(settings => box.Text = SettingsSchema.Read(settings, entry.Key));
        return box;
    }

    private Control Link(string text, Action click)
    {
        var link = new Border
        {
            Padding = new Thickness(12, 7),
            CornerRadius = new CornerRadius(10),
            Background = _skin.Brush(_skin.Surface2, 0.8),
            BorderBrush = _skin.Brush(_skin.Line),
            BorderThickness = new Thickness(1),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = new TextBlock { Text = text, FontSize = 13, Foreground = _skin.Brush(_skin.Text, 0.9) },
        };

        link.PointerPressed += (_, e) =>
        {
            e.Handled = true;
            click();
        };

        return link;
    }

    /// <summary>Opens settings.yaml in whatever the user edits text with.</summary>
    private void Reveal(string path)
    {
        try
        {
            SettingsFile.WriteDefaultIfMissing(path);
            using var opening = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true },
            };

            opening.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            Complain($"Could not open {path}: {ex.Message}");
        }
    }
}

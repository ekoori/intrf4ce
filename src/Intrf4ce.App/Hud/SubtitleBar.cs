using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using Intrf4ce.Core.Narration;

namespace Intrf4ce.App.Hud;

/// <summary>
/// The lower third: the sentence being spoken, with the word being spoken lit.
/// </summary>
/// <remarks>
/// Spoken words are full white, the current word is in the focus colour, and what is still to
/// come is dimmed, so the eye can follow the voice without reading ahead. The line is only
/// rebuilt when the word changes, not every frame.
/// </remarks>
public sealed class SubtitleBar : Border
{
    private readonly Skin _skin;
    private readonly TextBlock _text;
    private TextSpan _caption = new(-1, -1);
    private int _word = -2;

    public SubtitleBar(Skin skin)
    {
        _skin = skin;
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Bottom;
        Margin = new Thickness(0, 0, 0, 84);
        MaxWidth = 1100;
        Padding = new Thickness(26, 12);
        CornerRadius = new CornerRadius(14);
        Background = new SolidColorBrush(Color.FromArgb(205, 0x07, 0x0A, 0x0E));
        IsHitTestVisible = false;
        Opacity = 0;
        Transitions = [new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(250) }];

        _text = new TextBlock
        {
            FontSize = 25,
            LineHeight = 34,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            FontWeight = FontWeight.Medium,
        };

        Child = _text;
    }

    public bool Enabled { get; set; } = true;

    /// <summary>Shows the caption containing <paramref name="offset"/> with that word lit.</summary>
    public void Show(string text, IReadOnlyList<TextSpan> captions, IReadOnlyList<TextSpan> words, int offset)
    {
        if (!Enabled || captions.Count == 0)
        {
            Hide();
            return;
        }

        var caption = captions.FirstOrDefault(c => offset < c.End);
        if (caption == default)
        {
            caption = captions[^1];
        }

        var word = -1;
        for (var i = 0; i < words.Count; i++)
        {
            if (words[i].Start <= offset && offset < words[i].End + 1)
            {
                word = i;
                break;
            }
        }

        if (caption != _caption || word != _word)
        {
            _caption = caption;
            _word = word;
            Build(text, caption, word >= 0 ? words[word] : default, offset);
        }

        Opacity = 1;
    }

    public void Hide()
    {
        Opacity = 0;
        _caption = new TextSpan(-1, -1);
        _word = -2;
    }

    private void Build(string text, TextSpan caption, TextSpan word, int offset)
    {
        var inlines = _text.Inlines!;
        inlines.Clear();

        var start = caption.Start;
        var end = caption.End;

        // Before the current word: spoken.
        var spokenEnd = word.Length > 0 ? Math.Clamp(word.Start, start, end) : Math.Clamp(offset, start, end);
        if (spokenEnd > start)
        {
            inlines.Add(new Run(text[start..spokenEnd]) { Foreground = _skin.Brush(_skin.Text) });
        }

        if (word.Length > 0 && word.Start >= start && word.End <= end)
        {
            inlines.Add(new Run(text[word.Start..word.End]) { Foreground = _skin.Brush(_skin.Accent) });
            spokenEnd = word.End;
        }

        if (spokenEnd < end)
        {
            inlines.Add(new Run(text[spokenEnd..end]) { Foreground = _skin.Brush(_skin.Text, 0.5) });
        }
    }
}

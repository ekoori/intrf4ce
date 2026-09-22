using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Intrf4ce.Core.Model;

namespace Intrf4ce.App.Stage.Elements;

/// <summary>
/// A question with lettered options. Checking the answer marks it right or wrong, shows the
/// explanation, and tells the agent what was chosen.
/// </summary>
/// <remarks>
/// Without an answer key the quiz is a poll: the choice goes to the agent and nothing is marked.
/// An answer survives the agent re-sending the same question (a slide update elsewhere on the
/// slide); a different question starts fresh.
/// </remarks>
public sealed class QuizView : ElementView
{
    private readonly StackPanel _root = new() { Spacing = 22, Margin = new Thickness(0, 12, 0, 0) };
    private readonly List<OptionCard> _cards = [];
    private readonly HashSet<int> _selected = [];
    private Border? _check;
    private string _asked = "";
    private bool _answered;

    public QuizView(ElementContext context, QuizElement element)
        : base(context, element)
    {
        Children.Add(new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _root,
        });
    }

    private QuizElement Quiz => (QuizElement)Element;

    public override string Describe(string? part) => Quiz.Question;

    protected override Task LoadAsync(Element element, CancellationToken cancellationToken)
    {
        var quiz = (QuizElement)element;

        var asked = string.Join("\n", [quiz.Question, .. quiz.Options, quiz.Multiple ? "multiple" : "single"]);
        if (asked != _asked)
        {
            _asked = asked;
            _selected.Clear();
            _answered = false;
        }

        Build();
        return Task.CompletedTask;
    }

    private void Build()
    {
        var quiz = Quiz;
        _root.Children.Clear();
        _cards.Clear();

        _root.Children.Add(new TextBlock
        {
            Text = quiz.Question,
            FontSize = 34,
            FontWeight = FontWeight.SemiBold,
            LineHeight = 44,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Skin.Brush(Skin.Text),
        });

        if (quiz.Multiple)
        {
            _root.Children.Add(new TextBlock
            {
                Text = "Choose all that apply",
                FontSize = 18,
                Foreground = Skin.Brush(Skin.Muted),
                Margin = new Thickness(0, -10, 0, 0),
            });
        }

        // Short options sit two to a row; long ones get the full width.
        var twoColumns = quiz.Options.Count >= 4 && quiz.Options.All(o => o.Length <= 28);
        var options = new UniformGrid { Columns = twoColumns ? 2 : 1 };
        for (var i = 0; i < quiz.Options.Count; i++)
        {
            var card = new OptionCard(Skin, i, quiz.Options[i]) { Margin = new Thickness(0, 0, twoColumns ? 12 : 0, 12) };
            card.Clicked += (_, index) => Toggle(index);
            _cards.Add(card);
            options.Children.Add(card);
        }

        _root.Children.Add(options);

        _check = Button(quiz.Answer is null ? "Send answer" : "Check answer", Check);
        _root.Children.Add(_check);

        if (_answered)
        {
            ShowResult();
        }

        Refresh();
    }

    private void Toggle(int index)
    {
        if (_answered)
        {
            return;
        }

        if (!Quiz.Multiple)
        {
            _selected.Clear();
            _selected.Add(index);
        }
        else if (!_selected.Remove(index))
        {
            _selected.Add(index);
        }

        Refresh();
    }

    private void Check()
    {
        if (_answered || _selected.Count == 0)
        {
            return;
        }

        _answered = true;
        ShowResult();
        Refresh();

        var quiz = Quiz;
        var chosen = _selected.Order().ToList();
        var picked = string.Join("; ", chosen.Select(i => $"{Letter(i)}. {quiz.Options[i]}"));
        var meta = new Dictionary<string, string>
        {
            ["slide"] = SlideId,
            ["element"] = Id,
            ["selected"] = string.Join(",", chosen),
        };

        string text;
        if (quiz.Answer is { } answer)
        {
            var correct = IsCorrect(answer);
            meta["correct"] = correct ? "true" : "false";
            text = $"Quiz \"{quiz.Question}\": the user chose {picked}, which is {(correct ? "correct" : "wrong")}"
                + (correct ? "." : $"; the answer is {string.Join("; ", answer.Order().Select(i => $"{Letter(i)}. {quiz.Options[i]}"))}.");
        }
        else
        {
            text = $"Poll \"{quiz.Question}\": the user chose {picked}.";
        }

        Context.Publish("quiz", text, meta);
    }

    private bool IsCorrect(IReadOnlyList<int> answer) => _selected.SetEquals(answer);

    /// <summary>Right or wrong, the answer shown, and the explanation.</summary>
    private void ShowResult()
    {
        var quiz = Quiz;
        var result = new StackPanel { Spacing = 10 };

        if (quiz.Answer is { } answer)
        {
            var correct = IsCorrect(answer);
            result.Children.Add(new TextBlock
            {
                Text = correct ? "Correct." : $"Not quite: the answer is {string.Join(" and ", answer.Order().Select(Letter))}.",
                FontSize = 24,
                FontWeight = FontWeight.SemiBold,
                Foreground = Skin.Brush(correct ? Skin.Success : Skin.Danger),
            });
        }
        else
        {
            result.Children.Add(new TextBlock
            {
                Text = "Thanks, your answer went to the presenter.",
                FontSize = 22,
                Foreground = Skin.Brush(Skin.Cool),
            });
        }

        if (!string.IsNullOrWhiteSpace(quiz.Explanation))
        {
            result.Children.Add(new TextBlock
            {
                Text = quiz.Explanation,
                FontSize = 21,
                LineHeight = 30,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Skin.Brush(Skin.Text, 0.9),
            });
        }

        _root.Children.Add(new Border
        {
            BorderBrush = Skin.Brush(Skin.Accent, 0.8),
            BorderThickness = new Thickness(3, 0, 0, 0),
            Padding = new Thickness(18, 4, 0, 4),
            Child = result,
        });
    }

    private void Refresh()
    {
        var answer = Quiz.Answer;
        foreach (var card in _cards)
        {
            var selected = _selected.Contains(card.Index);
            card.State = !_answered ? (selected ? OptionState.Selected : OptionState.Open)
                : answer is null ? (selected ? OptionState.Selected : OptionState.Closed)
                : answer.Contains(card.Index) ? OptionState.Right
                : selected ? OptionState.Wrong
                : OptionState.Closed;
        }

        if (_check is not null)
        {
            _check.IsVisible = !_answered;
            _check.Opacity = _selected.Count > 0 ? 1 : 0.4;
        }
    }

    private static string Letter(int index) => ((char)('A' + Math.Min(index, 25))).ToString();

    private Border Button(string text, Action click)
    {
        var button = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(26, 12),
            Background = Skin.Brush(Skin.Accent),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = new TextBlock
            {
                Text = text,
                FontSize = 21,
                FontWeight = FontWeight.SemiBold,
                Foreground = Skin.Brush(Skin.Ink),
            },
        };

        Clickable(button, click);
        return button;
    }

    /// <summary>
    /// A press that is handled where it lands, so the stage underneath does not start a drag,
    /// and a click that counts only if released over the same control.
    /// </summary>
    private static void Clickable(Control control, Action click)
    {
        var pressed = false;
        control.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(control).Properties.IsLeftButtonPressed)
            {
                pressed = true;
                e.Handled = true;
            }
        };

        control.PointerReleased += (_, e) =>
        {
            if (pressed && new Rect(control.Bounds.Size).Contains(e.GetPosition(control)))
            {
                click();
            }

            pressed = false;
            e.Handled = true;
        };

        control.PointerCaptureLost += (_, _) => pressed = false;
        control.DoubleTapped += (_, e) => e.Handled = true;
    }

    private enum OptionState
    {
        Open,
        Selected,
        Right,
        Wrong,
        Closed,
    }

    /// <summary>One option: a letter badge and the text, in whatever state the question is in.</summary>
    private sealed class OptionCard : Border
    {
        private readonly Skin _skin;
        private readonly Border _badge;
        private readonly TextBlock _letter;
        private readonly TextBlock _text;
        private OptionState _state;
        private bool _hover;

        public OptionCard(Skin skin, int index, string text)
        {
            _skin = skin;
            Index = index;

            CornerRadius = new CornerRadius(14);
            BorderThickness = new Thickness(2);
            Padding = new Thickness(16, 14);
            Cursor = new Cursor(StandardCursorType.Hand);
            Transitions =
            [
                new BrushTransition { Property = BackgroundProperty, Duration = TimeSpan.FromMilliseconds(160) },
                new BrushTransition { Property = BorderBrushProperty, Duration = TimeSpan.FromMilliseconds(160) },
                new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(200) },
            ];

            _letter = new TextBlock
            {
                Text = Letter(index),
                FontSize = 19,
                FontWeight = FontWeight.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };

            _badge = new Border
            {
                Width = 38,
                Height = 38,
                CornerRadius = new CornerRadius(19),
                BorderThickness = new Thickness(2),
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 0, 16, 0),
                Child = _letter,
            };

            _text = new TextBlock
            {
                Text = text,
                FontSize = 23,
                LineHeight = 31,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
            };

            var row = new DockPanel();
            DockPanel.SetDock(_badge, Dock.Left);
            row.Children.Add(_badge);
            row.Children.Add(_text);
            Child = row;

            Clickable(this, () => Clicked?.Invoke(this, Index));
            PointerEntered += (_, _) =>
            {
                _hover = true;
                Apply();
            };
            PointerExited += (_, _) =>
            {
                _hover = false;
                Apply();
            };

            Apply();
        }

        public event EventHandler<int>? Clicked;

        public int Index { get; }

        public OptionState State
        {
            get => _state;
            set
            {
                _state = value;
                Apply();
            }
        }

        private void Apply()
        {
            var (edge, fill, badge, ink) = _state switch
            {
                OptionState.Selected => (_skin.Accent, _skin.Brush(_skin.Accent, 0.14), _skin.Brush(_skin.Accent), _skin.Ink),
                OptionState.Right => (_skin.Success, _skin.Brush(_skin.Success, 0.14), _skin.Brush(_skin.Success), _skin.Ink),
                OptionState.Wrong => (_skin.Danger, _skin.Brush(_skin.Danger, 0.14), _skin.Brush(_skin.Danger), _skin.Ink),
                _ => (_hover && _state == OptionState.Open ? _skin.Muted : _skin.Line, _skin.Brush(_skin.Surface2, 0.7), (IBrush)Brushes.Transparent, _skin.Muted),
            };

            BorderBrush = new SolidColorBrush(edge);
            Background = fill;
            _badge.Background = badge;
            _badge.BorderBrush = new SolidColorBrush(edge);
            _letter.Foreground = new SolidColorBrush(ink);
            _letter.Text = _state switch
            {
                OptionState.Right => "✓",
                OptionState.Wrong => "✗",
                _ => Letter(Index),
            };

            _text.Foreground = _skin.Brush(_skin.Text, _state == OptionState.Closed ? 0.55 : 1);
            Cursor = _state is OptionState.Open or OptionState.Selected ? new Cursor(StandardCursorType.Hand) : Cursor.Default;
        }
    }
}

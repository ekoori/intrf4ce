using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Intrf4ce.Agent.Hosted;

namespace Intrf4ce.App.Hud;

/// <summary>
/// The conversation's way back in when the agent is signed out: one card that asks, waits on
/// the browser, and says how it went.
/// </summary>
/// <remarks>
/// A card rather than a message, because the thing to do is press a button. What the agent
/// itself says — Claude Code's "please run /login" — is an instruction for a terminal intrf4ce
/// does not have, so it is kept small, at the foot, for whoever wants to know exactly what went
/// wrong.
/// </remarks>
public sealed class SignInCard : Border
{
    private readonly Skin _theme;
    private readonly AgentIdentity _agent;
    private readonly TextBlock _title;
    private readonly SelectableTextBlock _text;
    private readonly Border _button;
    private readonly TextBlock _buttonText;
    private readonly TextBlock _page;
    private readonly SelectableTextBlock _said;
    private Action? _click;
    private string? _address;
    private string? _lastSaid;
    private bool _askAgain;
    private bool _asking;

    public SignInCard(Skin theme, AgentIdentity agent)
    {
        _theme = theme;
        _agent = agent;
        CornerRadius = new CornerRadius(12);
        Padding = new Thickness(14, 12);
        BorderThickness = new Thickness(1);
        Background = new SolidColorBrush(Color.FromArgb(220, 0x1A, 0x17, 0x10));

        _title = new TextBlock
        {
            FontSize = 15,
            FontWeight = FontWeight.SemiBold,
            Foreground = theme.Brush(theme.Text),
            TextWrapping = TextWrapping.Wrap,
        };

        _text = new SelectableTextBlock
        {
            FontSize = 14,
            LineHeight = 21,
            Foreground = theme.Brush(theme.Text, 0.88),
            TextWrapping = TextWrapping.Wrap,
        };

        _buttonText = new TextBlock
        {
            FontSize = 14,
            FontWeight = FontWeight.SemiBold,
            Foreground = theme.Brush(theme.Ink),
        };

        _button = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(16, 8),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = _buttonText,
        };

        Clickable(_button, () => _click?.Invoke());

        // Only once the agent has said where its page is: for when no browser came up.
        _page = new TextBlock
        {
            Text = "No browser? Open the sign-in page",
            FontSize = 13,
            Foreground = theme.Brush(theme.Cool),
            TextDecorations = TextDecorations.Underline,
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand),
            IsVisible = false,
        };

        Clickable(_page, () =>
        {
            if (_address is { } address)
            {
                PageRequested?.Invoke(this, address);
            }
        });

        _said = new SelectableTextBlock
        {
            FontSize = 12,
            Foreground = theme.Brush(theme.Faint),
            TextWrapping = TextWrapping.Wrap,
        };

        _button.Margin = new Thickness(0, 4, 0, 0);
        Child = new StackPanel
        {
            Spacing = 8,
            Children = { _title, _text, _button, _page, _said },
        };

        Ask(null, askAgain: false);
    }

    /// <summary>Sign in, or try again.</summary>
    public event EventHandler? SignInRequested;

    /// <summary>Stop waiting on the browser.</summary>
    public event EventHandler? CancelRequested;

    /// <summary>The sign-in page, to open by hand.</summary>
    public event EventHandler<string>? PageRequested;

    /// <summary>Asks for a sign-in.</summary>
    /// <param name="said">What the agent said when it could not answer; null when nothing was asked yet.</param>
    /// <param name="askAgain">Whether something is waiting to be asked again once signed in.</param>
    public void Ask(string? said, bool askAgain)
    {
        _lastSaid = said;
        _askAgain = askAgain;
        Ask();
    }

    /// <summary>Back to asking, as it last asked: after a sign-in was given up.</summary>
    public void Ask()
    {
        _asking = true;
        Paint(_theme.Accent);
        _title.Text = $"{_agent.Name} is signed out";
        _text.Text = $"The agent works through your {_agent.Account} account, so it cannot answer until you sign in."
            + (_askAgain ? " What you asked goes to it again once you are in." : "");
        Button("Sign in", () => SignInRequested?.Invoke(this, EventArgs.Empty), primary: true);
        _page.IsVisible = false;
        Said(_lastSaid is { Length: > 0 } said ? $"{_agent.Name} said: {said}" : null);
    }

    /// <summary>What was asked went with the conversation it was asked in: nothing is asked again now.</summary>
    public void ForgetQuestion()
    {
        _askAgain = false;
        if (_asking)
        {
            Ask();
        }
    }

    /// <summary>The browser is open on the sign-in page, and the agent waits for it to come back.</summary>
    public void Wait()
    {
        _asking = false;
        _address = null;
        Paint(_theme.Accent);
        _title.Text = "Signing in…";
        _text.Text = $"{_agent.Account}'s sign-in page has opened in your browser. This carries on by itself once you are through.";
        Button("Cancel", () => CancelRequested?.Invoke(this, EventArgs.Empty), primary: false);
        _page.IsVisible = false;
        Said(null);
    }

    /// <summary>Where the sign-in page is, for when no browser came up.</summary>
    public void ShowPage(string address)
    {
        _address = address;
        _page.IsVisible = true;
        Reveal();
    }

    /// <summary>The page was opened by hand; for some agents its code then comes back by hand too.</summary>
    public void PageOpened()
    {
        _text.Text = _agent.CodeToPaste
            ? "Sign in on the page that opened. When it shows a code, paste it into the box below and press Enter."
            : "Sign in on the page that opened. This carries on by itself once you are through.";
        Reveal();
    }

    /// <summary>A code went to the agent, which is checking it.</summary>
    public void CodeTaken()
    {
        _text.Text = $"Checking the code with {_agent.Account}…";
        Reveal();
    }

    /// <summary>Signed in; the agent has been started again on the new login.</summary>
    public void Signed(string? account, bool askedAgain)
    {
        _asking = false;
        Paint(_theme.Success);
        _title.Text = account is { Length: > 0 } ? $"Signed in as {account}" : "Signed in";
        _text.Text = askedAgain ? "Asking the agent again what it could not answer." : "The agent is ready.";
        _button.IsVisible = false;
        _page.IsVisible = false;
        Said(null);
    }

    /// <summary>The sign-in did not work, and why.</summary>
    public void Fail(string why)
    {
        _asking = false;
        Paint(_theme.Danger);
        _title.Text = "The sign-in did not finish";
        _text.Text = why;
        Button("Try again", () => SignInRequested?.Invoke(this, EventArgs.Empty), primary: true);
        _page.IsVisible = false;
        Said(null);
    }

    private void Button(string text, Action click, bool primary)
    {
        _buttonText.Text = text;
        _buttonText.Foreground = primary ? _theme.Brush(_theme.Ink) : _theme.Brush(_theme.Text, 0.9);
        _button.Background = primary ? _theme.Brush(_theme.Accent) : _theme.Brush(_theme.Surface2, 0.9);
        _button.BorderBrush = primary ? null : _theme.Brush(_theme.Line);
        _button.BorderThickness = new Thickness(primary ? 0 : 1);
        _button.IsVisible = true;
        _click = click;
    }

    private void Said(string? text)
    {
        _said.Text = text;
        _said.IsVisible = text is not null;
        Reveal();
    }

    /// <summary>
    /// Scrolls to the card once it is laid out again: one that grew would otherwise run past the
    /// foot of the conversation, with its button out of sight.
    /// </summary>
    private void Reveal() => Dispatcher.UIThread.Post(() => this.BringIntoView(), DispatcherPriority.Background);

    private void Paint(Color colour) => BorderBrush = _theme.Brush(colour, 0.55);

    /// <summary>A click that counts only when released over the control it was pressed on.</summary>
    private static void Clickable(Control control, Action click)
    {
        var pressed = false;
        control.PointerPressed += (_, e) =>
        {
            pressed = e.GetCurrentPoint(control).Properties.IsLeftButtonPressed;
            e.Handled = true;
        };

        control.PointerReleased += (_, e) =>
        {
            if (pressed && control.IsPointerOver)
            {
                click();
            }

            pressed = false;
            e.Handled = true;
        };
    }
}

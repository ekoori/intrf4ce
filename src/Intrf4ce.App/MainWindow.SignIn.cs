using System.ComponentModel;
using Avalonia.Threading;
using Intrf4ce.Agent.Hosted;
using Intrf4ce.App.Hud;

namespace Intrf4ce.App;

/// <summary>
/// Signing the agent back in when it is signed out, without a terminal: a card in the
/// conversation, the browser, and the agent carrying on with what it was asked.
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>The card that signs the agent in, while one is wanted.</summary>
    private SignInCard? _signIn;

    /// <summary>The sign-in under way, to give it up; null while there is none.</summary>
    private CancellationTokenSource? _signingIn;

    /// <summary>
    /// The agent is signed out: the card that signs it in, at the foot of the conversation,
    /// beside the question it could not answer.
    /// </summary>
    /// <param name="said">What the agent said; empty when that was found out before anything was asked.</param>
    private void OfferSignIn(string said)
    {
        var card = _signIn ??= NewSignInCard();

        // A sign-in under way says so itself, and asks again whatever failed meanwhile.
        if (_signingIn is null)
        {
            card.Ask(said.Length > 0 ? said : null, askAgain: said.Length > 0);
        }

        _chat.AddCard(card);
        if (_chat.Collapsed)
        {
            _chat.Collapse(false);
        }

        Log.Write($"{_agent?.Identity.Name} is signed out" + (said.Length > 0 ? ": " + said : ""));
    }

    /// <summary>
    /// The conversation was emptied for another: the card stays while it is wanted, but what
    /// was asked stayed with the conversation it was asked in.
    /// </summary>
    private void KeepSignInCard()
    {
        if (_signIn is { } card)
        {
            card.ForgetQuestion();
            _chat.AddCard(card);
        }
    }

    private SignInCard NewSignInCard()
    {
        var card = new SignInCard(_theme, _agent?.Identity ?? new AgentIdentity("The agent", "agent", CodeToPaste: false));
        card.SignInRequested += (_, _) => _ = SignIn();
        card.CancelRequested += (_, _) => CancelSignIn();
        card.PageRequested += (_, address) => OpenSignInPage(card, address);
        return card;
    }

    /// <summary>
    /// Signs the agent in, in the browser, with the card following along. Once it is done the
    /// agent carries on with the new login, and is asked again what it could not answer.
    /// </summary>
    private async Task SignIn()
    {
        if (_agent is null)
        {
            return;
        }

        _signingIn?.Cancel();
        using var attempt = new CancellationTokenSource();
        _signingIn = attempt;
        var card = _signIn ??= NewSignInCard();
        card.Wait();
        _chat.AddCard(card);
        UpdateHud();

        SignInOutcome outcome;
        try
        {
            outcome = await _agent.SignInAsync(
                address => Dispatcher.UIThread.Post(() =>
                {
                    if (_signingIn == attempt)
                    {
                        card.ShowPage(address);
                    }
                }),
                attempt.Token);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or Win32Exception)
        {
            Log.Error("signing the agent in", ex);
            outcome = new SignInOutcome(false, ex.Message);
        }

        // Given up, or overtaken by another try, which speaks for itself; a sign-in that
        // worked all the same is still worth saying.
        if (!outcome.SignedIn && _signingIn != attempt)
        {
            return;
        }

        if (_signingIn == attempt)
        {
            _signingIn = null;
        }

        if (outcome.SignedIn)
        {
            card.Signed(outcome.Account, outcome.AskedAgain);
            _signIn = null;
            if (outcome.AskedAgain)
            {
                ShowSent();
            }
        }
        else
        {
            card.Fail(outcome.Error ?? $"{_agent.Identity.Name} stopped before it was signed in.");
        }

        UpdateHud();
    }

    /// <summary>Gives up the sign-in under way: asking again while still signed out, gone otherwise.</summary>
    private void CancelSignIn()
    {
        if (_signingIn is not { } attempt)
        {
            return;
        }

        _signingIn = null;
        attempt.Cancel();
        if (_agent?.SignedOut == true)
        {
            _signIn?.Ask();
        }
        else if (_signIn is { } card)
        {
            _chat.RemoveCard(card);
            _signIn = null;
        }

        UpdateHud();
    }

    /// <summary>The sign-in page, opened by hand when no browser came up: it ends with a code for the box below.</summary>
    private void OpenSignInPage(SignInCard card, string address)
    {
        if (Shell.Open(address) is { } error)
        {
            _chat.AddMessage(fromUser: false, $"{error}. The sign-in page is {address}", isError: true);
            return;
        }

        card.PageOpened();
        _chat.FocusInput();
    }

    /// <summary>
    /// A sign-in code, typed or pasted where the agent is talked to while a sign-in waits for
    /// one: it goes to the agent's sign-in, and is neither shown nor kept with the presentation.
    /// </summary>
    private bool TakeSignInCode(string text)
    {
        if (_signingIn is null || _agent?.SubmitSignInCode(text) != true)
        {
            return false;
        }

        _signIn?.CodeTaken();
        return true;
    }
}

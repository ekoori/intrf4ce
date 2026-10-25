using System.Text.Json.Nodes;
using Intrf4ce.Agent.Hosted;
using Intrf4ce.Core.Settings;

namespace Intrf4ce.Tests.Agent;

/// <summary>
/// Claude Code signed out, as it says so: the messages below are what <c>claude -p</c>
/// 2.1.280 printed with an expired login and with none at all, trimmed of fields nobody reads.
/// </summary>
public sealed class SignInTests
{
    private const string Expired =
        """
        {"type":"assistant","message":{"id":"08c36927-731c-4579-a859-b93050ea1fbc","model":"<synthetic>","role":"assistant","stop_reason":"stop_sequence","type":"message","usage":{"input_tokens":0,"output_tokens":0},"content":[{"type":"text","text":"Failed to authenticate: OAuth session expired and could not be refreshed"}]},"parent_tool_use_id":null,"session_id":"d3e7e3f0-2286-43b6-998e-c293b86e9209","uuid":"6e58d3e7-6519-473c-90ac-ff9d331491a0","error":"authentication_failed","is_api_error_message":true}
        """;

    private const string ExpiredResult =
        """
        {"type":"result","subtype":"success","is_error":true,"duration_ms":226,"num_turns":1,"result":"Failed to authenticate: OAuth session expired and could not be refreshed","session_id":"d3e7e3f0-2286-43b6-998e-c293b86e9209","total_cost_usd":0,"terminal_reason":"api_error"}
        """;

    private const string RateLimited =
        """
        {"type":"assistant","message":{"model":"<synthetic>","role":"assistant","type":"message","content":[{"type":"text","text":"You've hit your session limit · resets 12pm (Australia/Sydney)"}]},"parent_tool_use_id":null,"session_id":"91ab5c73-c4a7-4b72-9c16-f063a9cc361a","error":"rate_limit","is_api_error_message":true}
        """;

    private const string RateLimitedResult =
        """
        {"type":"result","subtype":"success","is_error":true,"result":"You've hit your session limit · resets 12pm (Australia/Sydney)","session_id":"91ab5c73-c4a7-4b72-9c16-f063a9cc361a","total_cost_usd":0}
        """;

    [Fact]
    public void AnExpiredLoginAsksForASignInRatherThanSpeakingForTheAgent()
    {
        var (host, seen) = Host();

        host.Handle(Parse(Expired));
        host.Handle(Parse(ExpiredResult));

        var asked = Assert.Single(seen, a => a.Kind == AgentActivityKind.SignInNeeded);
        Assert.Equal("Failed to authenticate: OAuth session expired and could not be refreshed", asked.Text);
        Assert.DoesNotContain(seen, a => a.Kind is AgentActivityKind.Said or AgentActivityKind.Error);
        Assert.Equal(AgentActivityKind.TurnDone, seen[^1].Kind);
        Assert.True(host.SignedOut);
    }

    [Fact]
    public void NeverHavingSignedInIsTheSameAsHavingExpired()
    {
        var (host, seen) = Host();

        host.Handle(Parse(Expired.Replace("Failed to authenticate: OAuth session expired and could not be refreshed", "Not logged in · Please run /login", StringComparison.Ordinal)));
        host.Handle(Parse(ExpiredResult));

        Assert.Equal("Not logged in · Please run /login", Assert.Single(seen, a => a.Kind == AgentActivityKind.SignInNeeded).Text);
    }

    [Fact]
    public void ATurnThatGetsThroughMeansItIsSignedInAgain()
    {
        var (host, _) = Host();
        host.Handle(Parse(Expired));
        host.Handle(Parse(ExpiredResult));

        host.Handle(Parse("""{"type":"result","subtype":"success","is_error":false,"result":"done","session_id":"d3e7e3f0-2286-43b6-998e-c293b86e9209","total_cost_usd":0.01}"""));

        Assert.False(host.SignedOut);
    }

    /// <summary>
    /// Claude Code's other refusals are errors too, not lines the agent said: once in red, and
    /// not kept with the presentation as if it were part of the conversation.
    /// </summary>
    [Fact]
    public void AnyOtherRefusalFromClaudeCodeIsOneErrorAndNotTheAgentTalking()
    {
        var (host, seen) = Host();

        host.Handle(Parse(RateLimited));
        host.Handle(Parse(RateLimitedResult));

        var error = Assert.Single(seen, a => a.Kind == AgentActivityKind.Error);
        Assert.Equal("You've hit your session limit · resets 12pm (Australia/Sydney)", error.Text);
        Assert.DoesNotContain(seen, a => a.Kind is AgentActivityKind.Said or AgentActivityKind.SignInNeeded);
        Assert.False(host.SignedOut);
    }

    [Fact]
    public void WhatTheAgentWritesIsStillWhatItSaid()
    {
        var (host, seen) = Host();

        host.Handle(Parse("""{"type":"assistant","message":{"model":"claude-opus-5-5","role":"assistant","content":[{"type":"text","text":"About $250 would be fair."}]},"parent_tool_use_id":null,"session_id":"s"}"""));

        Assert.Equal(AgentActivityKind.Said, Assert.Single(seen).Kind);
    }

    [Fact]
    public void StatusReadsWhetherItIsSignedInAndAsWhom()
    {
        var expired = ClaudeSignIn.ParseStatus(
            """
            {
              "loggedIn": false,
              "authMethod": "none",
              "apiProvider": "firstParty",
              "analyticsDisabled": false,
              "projectsDirectory": "C:\\Users\\someone\\.claude\\projects",
              "configDirectory": "C:\\Users\\someone\\.claude"
            }
            """);

        var signedIn = ClaudeSignIn.ParseStatus(
            """{"loggedIn": true, "authMethod": "claude.ai", "apiProvider": "firstParty", "email": "someone@example.com", "subscriptionType": "max"}""");

        Assert.Equal(new SignInStatus(false, null), expired);
        Assert.Equal(new SignInStatus(true, "someone@example.com"), signedIn);
    }

    [Theory]
    [InlineData("")]
    [InlineData("error: unknown command 'auth'")]
    [InlineData("{\"authMethod\": \"none\"}")]
    public void StatusThatSaysNothingReadableIsNotAnAnswer(string output) =>
        Assert.Null(ClaudeSignIn.ParseStatus(output));

    [Fact]
    public void StatusIsFoundAmongWhateverElseWasPrinted() =>
        Assert.Equal(new SignInStatus(false, null), ClaudeSignIn.ParseStatus("A newer version is available\n{\"loggedIn\": false}\n"));

    [Theory]
    [InlineData("If the browser didn't open, visit: https://claude.com/cai/oauth/authorize?code=true&client_id=abc&state=xyz")]
    // How it prints in a terminal that draws links: the address inside OSC 8, then again in colour.
    [InlineData("If the browser didn't open, visit: \u001b]8;;https://claude.com/cai/oauth/authorize?code=true&client_id=abc&state=xyz\u0007\u001b[94mhttps://claude.com/cai/oauth/authorize?code=true&client_id=abc&state=xyz\u001b[39m\u001b]8;;\u0007")]
    public void ThePageToOpenIsTakenFromTheLoginsOutput(string line) =>
        Assert.Equal("https://claude.com/cai/oauth/authorize?code=true&client_id=abc&state=xyz", ClaudeSignIn.PageIn(line));

    /// <summary>The line as Claude Code 2.1.280 prints it into a pipe, its random parts made up.</summary>
    [Fact]
    public void ThePageIsTheWholeAddressClaudeCodePrints()
    {
        const string page = "https://claude.com/cai/oauth/authorize?code=true&client_id=00000000-0000-0000-0000-000000000000"
            + "&response_type=code&redirect_uri=https%3A%2F%2Fplatform.claude.com%2Foauth%2Fcode%2Fcallback"
            + "&scope=org%3Acreate_api_key+user%3Aprofile+user%3Ainference+user%3Asessions%3Aclaude_code"
            + "&code_challenge=Zm9vYmFyYmF6&code_challenge_method=S256&state=c3RhdGVzdGF0ZXN0YXRl";

        Assert.Equal(page, ClaudeSignIn.PageIn("If the browser didn't open, visit: " + page));
    }

    [Theory]
    [InlineData("Opening browser to sign in…")]
    [InlineData("Paste code here if prompted > Login successful.")]
    public void LinesWithoutAPageHaveNone(string line) => Assert.Null(ClaudeSignIn.PageIn(line));

    [Theory]
    [InlineData("Xk3dPq81zLmN0vYtRw#9fJ2kLqP0aZx7cVbN4mTw", true)]
    [InlineData("  Xk3dPq81zLmN0vYtRw#9fJ2kLqP0aZx7cVbN4mTw  ", false)]
    [InlineData("issue#42", false)]
    [InlineData("C#", false)]
    [InlineData("what is in chapter#three of this", false)]
    [InlineData("https://example.com/guide#introduction", false)]
    [InlineData("first#second#third-part", false)]
    public void OnlyWhatLooksLikeASignInCodeIsTakenForOne(string text, bool code) =>
        Assert.Equal(code, ClaudeSignIn.LooksLikeCode(text));

    [Fact]
    public void WhyASignInFailedIsClaudeCodesOwnLine()
    {
        Assert.Equal(
            "Login failed: Request failed with status code 400",
            ClaudeSignIn.Reason("Warning: something unrelated\nLogin failed: Request failed with status code 400\n"));
        Assert.Null(ClaudeSignIn.Reason("  \n"));
    }

    private static (ClaudeCodeHost Host, List<AgentActivity> Seen) Host()
    {
        var host = new ClaudeCodeHost(
            new HostedAgentOptions(new AgentSettings(), "intrf4ce.exe", Path.Combine(Path.GetTempPath(), "intrf4ce-signin-tests"), "presenter"),
            _ => { });

        var seen = new List<AgentActivity>();
        host.Activity += (_, activity) => seen.Add(activity);
        return (host, seen);
    }

    private static JsonObject Parse(string json) => (JsonObject)JsonNode.Parse(json)!;
}

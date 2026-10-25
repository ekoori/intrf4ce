using System.Text.Json.Nodes;
using Intrf4ce.Agent.Hosted;
using Intrf4ce.Core.Settings;

namespace Intrf4ce.Tests.Agent;

/// <summary>
/// Codex's app server as it speaks: the messages below are what <c>codex app-server</c> 0.145.0
/// sent, trimmed of fields nobody reads, the signed-out ones from a home with no login.
/// </summary>
public sealed class CodexTests
{
    private const string Thread = "01a17191-b233-7a73-89cb-67d57652a32b";
    private const string Turn = "01a17191-b292-7e50-8ec0-7ae193676cc8";

    private const string Unauthorized =
        """
        {"method":"error","params":{"error":{"message":"Reconnecting... 2/5","codexErrorInfo":{"responseStreamDisconnected":{"httpStatusCode":401}},"additionalDetails":"unexpected status 401 Unauthorized: Missing bearer or basic authentication in header, url: wss://api.openai.com/v1/responses, cf-ray: a4fee5f3e8afe699-MEL"},"willRetry":true,"threadId":"01a17191-b233-7a73-89cb-67d57652a32b","turnId":"01a17191-b292-7e50-8ec0-7ae193676cc8"}}
        """;

    [Fact]
    public void ARefusalForWantOfALoginAsksForASignInWhenTheTurnEnds()
    {
        var (host, seen) = Host();

        host.Handle(Parse(Unauthorized));
        Assert.DoesNotContain(seen, a => a.Kind == AgentActivityKind.SignInNeeded);

        host.Handle(Completed("interrupted"));

        var asked = Assert.Single(seen, a => a.Kind == AgentActivityKind.SignInNeeded);
        Assert.Equal("unexpected status 401 Unauthorized: Missing bearer or basic authentication in header", asked.Text);
        Assert.DoesNotContain(seen, a => a.Kind is AgentActivityKind.Error or AgentActivityKind.Said);
        Assert.Equal(AgentActivityKind.TurnDone, seen[^1].Kind);
        Assert.True(host.SignedOut);
    }

    [Fact]
    public void ATurnThatFailsUnauthorisedAsksForASignInToo()
    {
        var (host, seen) = Host();

        host.Handle(Completed("failed", """{"message":"unauthorized","codexErrorInfo":"unauthorized","additionalDetails":null}"""));

        Assert.Equal("unauthorized", Assert.Single(seen, a => a.Kind == AgentActivityKind.SignInNeeded).Text);
    }

    [Fact]
    public void AnyOtherFailureIsAnError()
    {
        var (host, seen) = Host();

        host.Handle(Completed("failed", """{"message":"You've hit your usage limit.","codexErrorInfo":"usageLimitExceeded","additionalDetails":null}"""));

        Assert.Equal("You've hit your usage limit.", Assert.Single(seen, a => a.Kind == AgentActivityKind.Error).Text);
        Assert.False(host.SignedOut);
    }

    /// <summary>What Codex 0.145.0 said with a model chosen for a newer Codex.</summary>
    [Fact]
    public void ARefusalPassedOnAsJsonIsReadAsTheSentenceInIt()
    {
        const string body = """{"type":"error","status":400,"error":{"type":"invalid_request_error","message":"The 'gpt-6-astra' model requires a newer version of Codex. Please upgrade to the latest app or CLI and try again."}}""";

        Assert.Equal(
            "The 'gpt-6-astra' model requires a newer version of Codex. Please upgrade to the latest app or CLI and try again."
                + " Or give intrf4ce a model this Codex has, with agent.model in settings.yaml.",
            CodexHost.Readable(body));
        Assert.Equal("Something else went wrong.", CodexHost.Readable("Something else went wrong."));
        Assert.Equal("{not json", CodexHost.Readable("{not json"));
    }

    [Fact]
    public void ARetryIsSaidAsReconnectingNotAsAnError()
    {
        var (host, seen) = Host();

        host.Handle(Parse("""{"method":"error","params":{"error":{"message":"Reconnecting... 1/5","codexErrorInfo":{"responseStreamDisconnected":{"httpStatusCode":503}},"additionalDetails":"stream disconnected"},"willRetry":true,"threadId":"t","turnId":"u"}}"""));

        Assert.Equal(new AgentActivity(AgentActivityKind.Tool, "Reconnecting"), Assert.Single(seen));
    }

    [Fact]
    public void ATurnTheUserStoppedIsNotAFailure()
    {
        var (host, seen) = Host();

        host.Handle(Completed("interrupted"));

        Assert.DoesNotContain(seen, a => a.Kind is AgentActivityKind.Error or AgentActivityKind.SignInNeeded);
    }

    [Fact]
    public void WhatTheAgentWritesIsWhatItSaid()
    {
        var (host, seen) = Host();

        host.Handle(Item("item/completed", """{"type":"agentMessage","id":"m","text":"Building a five-slide tour of the knee.","phase":"commentary","memoryCitation":null}"""));
        host.Handle(Item("item/completed", """{"type":"agentMessage","id":"n","text":"  ","phase":"final_answer","memoryCitation":null}"""));

        Assert.Equal(new AgentActivity(AgentActivityKind.Said, "Building a five-slide tour of the knee."), Assert.Single(seen));
    }

    [Theory]
    [InlineData("""{"type":"mcpToolCall","id":"c","server":"intrf4ce","tool":"put_slide","status":"inProgress","arguments":{"slide":{"id":"knee","title":"The knee"}}}""", "Building slide “The knee”")]
    [InlineData("""{"type":"mcpToolCall","id":"c","server":"intrf4ce","tool":"get_state","status":"inProgress","arguments":{}}""", "")]
    [InlineData("""{"type":"mcpToolCall","id":"c","server":"weather","tool":"get_forecast","status":"inProgress","arguments":{}}""", "Using weather: get forecast")]
    [InlineData("""{"type":"commandExecution","id":"c","command":"dir C:\\photos","cwd":"C:\\","status":"inProgress"}""", "Running dir C:\\photos")]
    [InlineData("""{"type":"webSearch","id":"w","query":"knee anatomy","action":null,"results":null}""", "Searching the web for knee anatomy")]
    [InlineData("""{"type":"webSearch","id":"w","query":"","action":{"type":"search","query":"ACL tear","queries":null},"results":null}""", "Searching the web for ACL tear")]
    [InlineData("""{"type":"reasoning","id":"r","summary":[],"content":[]}""", "")]
    public void AStepOfATurnIsAStatusLine(string item, string line) =>
        Assert.Equal(line, CodexHost.Describe((JsonObject)JsonNode.Parse(item)!));

    [Fact]
    public void ErrorsAreToldApartByWhatCodexCallsThem()
    {
        Assert.True(CodexHost.IsUnauthorized(JsonNode.Parse("""{"codexErrorInfo":"unauthorized"}""")));
        Assert.True(CodexHost.IsUnauthorized(JsonNode.Parse("""{"codexErrorInfo":{"httpConnectionFailed":{"httpStatusCode":401}}}""")));
        Assert.False(CodexHost.IsUnauthorized(JsonNode.Parse("""{"codexErrorInfo":{"responseStreamDisconnected":{"httpStatusCode":503}}}""")));
        Assert.False(CodexHost.IsUnauthorized(JsonNode.Parse("""{"codexErrorInfo":{"activeTurnNotSteerable":{"turnKind":"review"}}}""")));
        Assert.False(CodexHost.IsUnauthorized(JsonNode.Parse("""{"codexErrorInfo":"usageLimitExceeded"}""")));
        Assert.False(CodexHost.IsUnauthorized(null));
    }

    [Theory]
    [InlineData("plan", "read-only")]
    [InlineData("bypassPermissions", "danger-full-access")]
    [InlineData("auto", "workspace-write")]
    [InlineData("dontAsk", "workspace-write")]
    public void ThePermissionModeChoosesTheSandbox(string mode, string sandbox) =>
        Assert.Equal(sandbox, CodexHost.Sandbox(mode));

    [Theory]
    [InlineData("claude", "", "claude")]
    [InlineData("codex", "", "codex")]
    // What a settings file written before there was a choice says: the provider is what changed.
    [InlineData("codex", "claude", "codex")]
    [InlineData("claude", "codex", "claude")]
    [InlineData("codex", @"C:\tools\codex.exe", @"C:\tools\codex.exe")]
    [InlineData("claude", @"C:\tools\claude.exe", @"C:\tools\claude.exe")]
    public void TheCommandIsTheProvidersOwnUnlessOneIsGiven(string provider, string command, string run) =>
        Assert.Equal(run, new AgentSettings { Provider = provider, Command = command }.CommandToRun);

    [Fact]
    public void TheAgentTheSettingsAskForIsTheOneStarted()
    {
        var home = Path.Combine(Path.GetTempPath(), "intrf4ce-codex-tests");
        Assert.IsType<CodexHost>(HostedAgent.Create(new HostedAgentOptions(new AgentSettings { Provider = "codex" }, "intrf4ce.exe", home, "presenter"), _ => { }));
        Assert.IsType<ClaudeCodeHost>(HostedAgent.Create(new HostedAgentOptions(new AgentSettings(), "intrf4ce.exe", home, "presenter"), _ => { }));
    }

    [Fact]
    public void ATranscriptIsLookedForOnlyByAThreadId()
    {
        var (host, _) = Host();

        Assert.Empty(host.TranscriptOf(@"..\..\secrets"));
        Assert.Empty(host.TranscriptOf(null));
    }

    private static (CodexHost Host, List<AgentActivity> Seen) Host()
    {
        var host = new CodexHost(
            new HostedAgentOptions(new AgentSettings { Provider = "codex" }, "intrf4ce.exe", Path.Combine(Path.GetTempPath(), "intrf4ce-codex-tests"), "presenter"),
            _ => { });

        var seen = new List<AgentActivity>();
        host.Activity += (_, activity) => seen.Add(activity);
        return (host, seen);
    }

    private static JsonObject Completed(string status, string error = "null") =>
        Parse($$"""{"method":"turn/completed","params":{"threadId":"{{Thread}}","turn":{"id":"{{Turn}}","items":[],"status":"{{status}}","error":""" + error + "}}}");

    private static JsonObject Item(string method, string item) =>
        Parse($$$"""{"method":"{{{method}}}","params":{"item":{{{item}}},"threadId":"{{{Thread}}}","turnId":"{{{Turn}}}"}}""");

    private static JsonObject Parse(string json) => (JsonObject)JsonNode.Parse(json)!;
}

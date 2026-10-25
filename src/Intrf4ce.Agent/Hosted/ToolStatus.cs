using System.Text.Json.Nodes;

namespace Intrf4ce.Agent.Hosted;

/// <summary>A tool call as a status line: "Searching the web for knee anatomy".</summary>
/// <remarks>
/// The same words whichever agent made the call: intrf4ce's own tools are named alike by
/// every agent, give or take the prefix its MCP client puts in front of them.
/// </remarks>
internal static class ToolStatus
{
    /// <param name="name">The tool as the agent names it.</param>
    /// <param name="input">Its arguments, for the part worth saying.</param>
    public static string Describe(string name, JsonObject? input)
    {
        string? Arg(string key) => input?[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

        var shortName = name.StartsWith("mcp__intrf4ce__", StringComparison.Ordinal) ? name["mcp__intrf4ce__".Length..] : name;

        return shortName switch
        {
            "WebSearch" => $"Searching the web for {Arg("query") ?? "…"}",
            "WebFetch" => $"Reading {Host(Arg("url"))}",
            "Read" => $"Reading {Path.GetFileName(Arg("file_path") ?? "a file")}",
            "Glob" or "Grep" => "Looking through files",
            "Bash" => "Running " + Short(Arg("command") ?? "a command"),
            "Task" => "Asking a helper to " + Short(Arg("description") ?? "look into something"),
            "TodoWrite" => "Planning",
            "put_slide" => $"Building slide “{input?["slide"]?["title"]?.GetValue<string>() ?? input?["slide"]?["id"]?.GetValue<string>() ?? "…"}”",
            "update_slide" => $"Updating slide {Arg("id")}",
            "new_deck" => $"Starting “{Arg("title")}”",
            "inspect_image" => "Studying an image",
            "scan_media" => "Looking through photos",
            "snapshot" => "Checking the stage",
            "wait_for_user" => "Listening",
            "set_playback" => "",
            "describe_deck" => "Looking at another session",
            "group_decks" or "rename_group" => "Filing sessions",
            "get_settings" or "set_setting" => "Checking the settings",
            "reply" or "say" or "get_state" or "go_to" or "focus" => "",
            _ => shortName.Replace('_', ' '),
        };
    }

    /// <summary>A command line short enough for a status line.</summary>
    public static string Short(string text) =>
        text.Length <= 48 ? text : text[..47].TrimEnd() + "…";

    public static string Host(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : "a page";
}

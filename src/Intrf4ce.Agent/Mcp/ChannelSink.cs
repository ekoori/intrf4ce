using System.Text.Json.Nodes;
using Intrf4ce.Core.Events;

namespace Intrf4ce.Agent.Mcp;

/// <summary>
/// Pushes user events into attached Claude Code sessions that were loaded as channels.
/// </summary>
/// <remarks>
/// Claude Code turns a <c>notifications/claude/channel</c> notification into a message in the
/// running session, so a chat typed into intrf4ce reaches the user's own terminal agent with
/// no polling. Delivery is not acknowledged, which is why only sessions that announced
/// themselves as channels (<c>intrf4ce --mcp --channel</c>) are pushed to.
/// </remarks>
public sealed class ChannelSink(McpServer server, Action<string> log) : IEventSink
{
    public bool TryDeliver(UserEvent userEvent)
    {
        var channels = server.Sessions.Where(s => s.Kind == SessionKind.Channel && s.IsInitialized).ToList();
        if (channels.Count == 0)
        {
            return false;
        }

        var meta = new JsonObject { ["type"] = userEvent.Type };
        foreach (var (key, value) in userEvent.Meta)
        {
            meta[key] = value;
        }

        var parameters = new JsonObject { ["content"] = userEvent.Text, ["meta"] = meta };
        foreach (var session in channels)
        {
            _ = session.NotifyAsync("notifications/claude/channel", (JsonObject)parameters.DeepClone())
                .ContinueWith(t => log("channel push failed: " + t.Exception?.GetBaseException().Message), TaskContinuationOptions.OnlyOnFaulted);
        }

        return true;
    }
}

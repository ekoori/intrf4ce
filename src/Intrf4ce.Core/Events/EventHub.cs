using System.Text;

namespace Intrf4ce.Core.Events;

/// <summary>Something the user did that the agent should hear about.</summary>
/// <param name="Type">chat, ask, quiz or nav.</param>
/// <param name="Text">The message itself, in words the agent can act on.</param>
/// <param name="Meta">Routing details: slide, element, target. Keys are identifiers.</param>
/// <param name="Sequence">Monotonic; how pollers know what they have seen.</param>
public sealed record UserEvent(
    string Type,
    string Text,
    IReadOnlyDictionary<string, string> Meta,
    DateTimeOffset At,
    long Sequence)
{
    /// <summary>How the event reads as a message to an agent that has no other channel.</summary>
    public string ToMessage()
    {
        if (Type == "chat" && Meta.Count == 0)
        {
            return Text;
        }

        var builder = new StringBuilder("<intrf4ce-event type=\"").Append(Type).Append('"');
        foreach (var (key, value) in Meta)
        {
            builder.Append(' ').Append(key).Append("=\"").Append(value.Replace("\"", "'", StringComparison.Ordinal)).Append('"');
        }

        return builder.Append('>').Append(Text).Append("</intrf4ce-event>").ToString();
    }
}

/// <summary>A way of getting events to an agent that can take them as they happen.</summary>
public interface IEventSink
{
    /// <summary>Delivers the event if this sink is live; false leaves it for the next sink or the poll queue.</summary>
    bool TryDeliver(UserEvent userEvent);
}

/// <summary>
/// Routes user events to whichever agent is listening.
/// </summary>
/// <remarks>
/// In order: the hosted agent's stdin, then attached sessions that opened as Claude Code
/// channels, then the queue that <c>wait_for_user</c> drains. An event goes to exactly one of
/// them. Sending it everywhere would have an attached agent answer a question the hosted one
/// already answered, and nothing in the channel protocol would let either know.
/// </remarks>
public sealed class EventHub
{
    private const int MaxQueued = 200;

    private readonly object _gate = new();
    private readonly List<IEventSink> _sinks = [];
    private readonly List<UserEvent> _queue = [];
    private TaskCompletionSource _arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _sequence;

    /// <summary>Raised for every event, delivered or queued, for the chat transcript and status line.</summary>
    public event EventHandler<UserEvent>? Published;

    public void AddSink(IEventSink sink)
    {
        lock (_gate)
        {
            _sinks.Add(sink);
        }
    }

    public void RemoveSink(IEventSink sink)
    {
        lock (_gate)
        {
            _sinks.Remove(sink);
        }
    }

    public UserEvent Publish(string type, string text, IReadOnlyDictionary<string, string>? meta = null)
    {
        UserEvent userEvent;
        IEventSink[] sinks;

        lock (_gate)
        {
            userEvent = new UserEvent(type, text, meta ?? new Dictionary<string, string>(), DateTimeOffset.Now, ++_sequence);
            sinks = [.. _sinks];
        }

        var delivered = false;
        foreach (var sink in sinks)
        {
            if (sink.TryDeliver(userEvent))
            {
                delivered = true;
                break;
            }
        }

        if (!delivered)
        {
            TaskCompletionSource arrived;
            lock (_gate)
            {
                _queue.Add(userEvent);
                if (_queue.Count > MaxQueued)
                {
                    _queue.RemoveAt(0);
                }

                arrived = _arrived;
                _arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            arrived.TrySetResult();
        }

        Published?.Invoke(this, userEvent);
        return userEvent;
    }

    /// <summary>
    /// Takes every queued event, waiting up to <paramref name="timeout"/> for the first.
    /// </summary>
    /// <returns>The events, oldest first; empty when nothing happened in time.</returns>
    public async Task<IReadOnlyList<UserEvent>> TakeAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;

        while (true)
        {
            Task arrived;
            lock (_gate)
            {
                if (_queue.Count > 0)
                {
                    var taken = _queue.ToList();
                    _queue.Clear();
                    return taken;
                }

                arrived = _arrived.Task;
            }

            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                return [];
            }

            try
            {
                await arrived.WaitAsync(remaining, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                return [];
            }
        }
    }

    /// <summary>How many events are waiting to be taken.</summary>
    public int Pending
    {
        get
        {
            lock (_gate)
            {
                return _queue.Count;
            }
        }
    }
}

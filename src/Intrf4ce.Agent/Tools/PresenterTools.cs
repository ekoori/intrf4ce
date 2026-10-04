using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Intrf4ce.Agent.Mcp;
using Intrf4ce.Core.Decks;
using Intrf4ce.Core.Events;
using Intrf4ce.Core.Layout;
using Intrf4ce.Core.Model;
using Intrf4ce.Core.Narration;
using Intrf4ce.Core.Settings;
using Intrf4ce.Core.Stage;

namespace Intrf4ce.Agent.Tools;

/// <summary>
/// Every tool in tools.json, implemented against the deck, the stage and the event hub.
/// </summary>
/// <remarks>
/// The dispatch table is the single list of implemented tools. A test compares it with the
/// schema file in both directions, so a tool cannot be described to the agent without being
/// implemented, or implemented and left undescribed.
/// </remarks>
public sealed class PresenterTools : IToolDispatcher
{
    private static readonly TimeSpan RenderWait = TimeSpan.FromSeconds(20);
    private const int MaxWaitSeconds = 600;

    private readonly DeckService _decks;
    private readonly IStage _stage;
    private readonly EventHub _events;
    private readonly IMediaTools _media;
    private readonly ISettingsAccess _settings;
    private readonly IReadOnlyDictionary<string, Func<JsonObject, McpSession?, CancellationToken, Task<ToolResult>>> _handlers;
    private IReadOnlyDictionary<string, PluginToolHandler> _pluginTools = new Dictionary<string, PluginToolHandler>();

    public PresenterTools(DeckService decks, IStage stage, EventHub events, IMediaTools media, ISettingsAccess settings)
    {
        _decks = decks;
        _stage = stage;
        _events = events;
        _media = media;
        _settings = settings;

        _handlers = new Dictionary<string, Func<JsonObject, McpSession?, CancellationToken, Task<ToolResult>>>(StringComparer.Ordinal)
        {
            ["get_state"] = (_, _, _) => Task.FromResult(GetState()),
            ["list_decks"] = (_, _, _) => Task.FromResult(ListDecks()),
            ["new_deck"] = (a, _, _) => Task.FromResult(NewDeck(a)),
            ["open_deck"] = (a, _, _) => Task.FromResult(OpenDeck(a)),
            ["put_slide"] = (a, _, c) => PutSlideAsync(a, c),
            ["update_slide"] = (a, _, c) => UpdateSlideAsync(a, c),
            ["remove_slide"] = (a, _, _) => Task.FromResult(RemoveSlide(a)),
            ["move_slide"] = (a, _, _) => Task.FromResult(MoveSlide(a)),
            ["go_to"] = (a, _, c) => GoToAsync(a, c),
            ["focus"] = (a, _, c) => FocusAsync(a, c),
            ["say"] = (a, _, c) => SayAsync(a, c),
            ["reply"] = (a, _, _) => Task.FromResult(Reply(a)),
            ["wait_for_user"] = (a, s, c) => WaitForUserAsync(a, s, c),
            ["snapshot"] = (a, _, c) => SnapshotAsync(a, c),
            ["inspect_image"] = (a, _, c) => InspectImageAsync(a, c),
            ["scan_media"] = (a, _, c) => ScanMediaAsync(a, c),
            ["set_playback"] = (a, _, c) => PlaybackAsync(a, c),
            ["get_settings"] = (_, _, _) => Task.FromResult(GetSettings()),
            ["set_setting"] = (a, _, _) => Task.FromResult(SetSetting(a)),
            ["describe_deck"] = (a, _, _) => Task.FromResult(DescribeDeck(a)),
            ["group_decks"] = (a, _, _) => Task.FromResult(GroupDecks(a)),
            ["rename_group"] = (a, _, _) => Task.FromResult(RenameGroup(a)),
            ["read_element"] = (a, _, c) => ReadElementAsync(a, c),
        };
    }

    /// <summary>intrf4ce's own tools; the ones plugins add are not among them.</summary>
    public IReadOnlyCollection<string> Implemented => [.. _handlers.Keys];

    /// <summary>Replaces the tools plugins add, by name.</summary>
    public void SetPluginTools(IReadOnlyDictionary<string, PluginToolHandler> tools) =>
        Volatile.Write(ref _pluginTools, tools ?? throw new ArgumentNullException(nameof(tools)));

    public Task<ToolResult> CallAsync(string name, JsonObject arguments, McpSession session, CancellationToken cancellationToken) =>
        CallAsync(name, arguments, cancellationToken, session);

    /// <summary>The same, without a session: for tests and the in-process self-test.</summary>
    public Task<ToolResult> CallAsync(string name, JsonObject arguments, CancellationToken cancellationToken, McpSession? session = null) =>
        _handlers.TryGetValue(name, out var handler) ? handler(arguments, session, cancellationToken)
        : Volatile.Read(ref _pluginTools).TryGetValue(name, out var plugin) ? CallPluginAsync(plugin, arguments, cancellationToken)
        : Task.FromResult(ToolResult.Error($"intrf4ce has no tool '{name}'"));

    /// <summary>A plugin's tool: its answer as it gave it, or what went wrong in words.</summary>
    private static async Task<ToolResult> CallPluginAsync(PluginToolHandler tool, JsonObject arguments, CancellationToken cancellationToken)
    {
        try
        {
            return ToolResult.Ok(await tool.Run(arguments, cancellationToken).ConfigureAwait(false));
        }
        catch (ArgumentException ex)
        {
            return ToolResult.Error(ex.Message);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Somebody else's code, so anything at all; it is the agent's to hear, not the session's to die of.
            return ToolResult.Error($"the {tool.Plugin} plugin could not answer: {ex.Message}");
        }
    }

    private async Task<ToolResult> ReadElementAsync(JsonObject arguments, CancellationToken cancellationToken)
    {
        var element = RequireString(arguments, "element");
        var slide = OptionalString(arguments, "slide");

        // slide.element, as focus targets are written.
        if (slide is null && element.Split('.') is [var before, var after])
        {
            (slide, element) = (before, after);
        }

        if (slide is not null && int.TryParse(slide, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
            && _decks.Current is { } deck && n >= 1 && n <= deck.Count)
        {
            slide = deck.Slides[n - 1].Id;
        }

        var reading = await _stage.ReadElementAsync(slide, element, cancellationToken).ConfigureAwait(false);
        if (reading is null)
        {
            return ToolResult.Error($"there is no element '{element}' on {(slide is null ? "the slide on stage" : $"slide '{slide}'")}; get_state lists them");
        }

        return ToolResult.Json(new
        {
            slide = reading.SlideId,
            element = reading.ElementId,
            type = reading.Type,
            live = reading.Live,
            shows = reading.Text,
            note = reading.Live
                ? "This changes by itself; read it again just before narrating it, and say it as of now."
                : "This shows what the slide says; it changes only when the slide does.",
        });
    }

    private ToolResult GetState()
    {
        var deck = _decks.Current;
        var state = _stage.State;

        if (deck is null)
        {
            return ToolResult.Json(new
            {
                deck = (object?)null,
                hint = "No deck is open. Call new_deck with a title, or list_decks and open_deck.",
                voice = state.Voice,
                agent = state.Agent,
                pendingEvents = _events.Pending,
            });
        }

        return ToolResult.Json(new
        {
            deck = new
            {
                id = deck.Id,
                title = deck.Title,
                slides = deck.Slides.Select((s, i) => new
                {
                    n = i + 1,
                    id = s.Id,
                    title = s.Title,
                    elements = s.Elements.Select(e => $"{e.Id} ({e.TypeName})"),
                    size = SlideLayout.IsCanvas(s) ? SlideLayout.SizeOf(s) is var (w, h) ? new[] { w, h } : null : null,
                    narrated = !string.IsNullOrWhiteSpace(s.Narration),
                    played = deck.Spoken.Contains(s.Id),
                }),
            },
            unplayed = _decks.Unspoken(),
            current = state.CurrentIndex < 0 ? null : new { n = state.CurrentIndex + 1, id = state.CurrentSlideId },
            playback = state.Playback,
            focus = state.Focus,
            userAtNewest = state.UserAtNewest,
            voice = state.Voice,
            agent = state.Agent,
            slideSize = new[] { 1600, 900 },
            pendingEvents = _events.Pending,
        });
    }

    private ToolResult ListDecks() =>
        ToolResult.Json(new
        {
            decks = _decks.Store.List().Select(d => new
            {
                id = d.Id,
                title = d.Title,
                about = d.Subject,
                group = d.Group,
                slides = d.Slides,
                modified = d.Modified.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
            }),
            open = _decks.Current?.Id,
        });

    /// <summary>
    /// Another session, read without disturbing the one on the stage.
    /// </summary>
    /// <remarks>
    /// Opening a deck to see what is in it would take the user's presentation off the stage,
    /// which is a rude way to answer "what did we say about this last week".
    /// </remarks>
    private ToolResult DescribeDeck(JsonObject arguments)
    {
        var id = RequireString(arguments, "id");
        Deck deck;
        try
        {
            deck = _decks.Current is { } open && open.Id == id ? open : _decks.Store.Load(id);
        }
        catch (Exception ex) when (ex is FileNotFoundException or ArgumentException or InvalidDataException)
        {
            var known = string.Join(", ", _decks.Store.List().Select(d => d.Id));
            throw new ToolInputException($"{ex.Message} Presentations: {(known.Length == 0 ? "none yet" : known)}.");
        }

        return ToolResult.Json(new
        {
            id = deck.Id,
            title = deck.Title,
            modified = deck.Modified.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
            open = deck.Id == _decks.Current?.Id,
            slides = deck.Slides.Select((s, i) => new
            {
                n = i + 1,
                id = s.Id,
                title = s.Title,
                about = Gist(s),
            }),
            conversation = deck.Chat.TakeLast(10).Select(c =>
                $"{c.Who}: {Gist(c.Text, 200)}{(c.Files is { Count: > 0 } files ? $" [attached: {string.Join(", ", files)}]" : "")}"),
            note = deck.Id == _decks.Current?.Id
                ? "this is the presentation on the stage"
                : "not open; open_deck puts it on the stage, which takes the current one off",
        });
    }

    private ToolResult GroupDecks(JsonObject arguments)
    {
        if (arguments["decks"] is not JsonArray list || list.Count == 0)
        {
            throw new ToolInputException("decks is required: the ids of the presentations to file, from list_decks");
        }

        var group = OptionalString(arguments, "group");
        var known = _decks.Store.List().Select(d => d.Id).ToHashSet(StringComparer.Ordinal);
        var ids = list.Select(d => d?.GetValue<string>() ?? "").ToList();
        var missing = ids.Where(id => !known.Contains(id)).ToList();
        if (missing.Count > 0)
        {
            throw new ToolInputException($"no presentation {string.Join(", ", missing)} (there are: {string.Join(", ", known.Order(StringComparer.Ordinal))})");
        }

        foreach (var id in ids)
        {
            _decks.SetGroup(id, group);
        }

        return ToolResult.Json(new
        {
            filed = ids,
            group = string.IsNullOrWhiteSpace(group) ? null : group.Trim(),
            groups = _decks.Store.List().Where(d => d.Group is not null).GroupBy(d => d.Group).Select(g => new { name = g.Key, sessions = g.Count() }),
        });
    }

    private ToolResult RenameGroup(JsonObject arguments)
    {
        var from = RequireString(arguments, "from");
        var to = RequireString(arguments, "to");
        var moved = _decks.RenameGroup(from, to);
        if (moved == 0)
        {
            var groups = _decks.Store.List().Select(d => d.Group).OfType<string>().Distinct().ToList();
            throw new ToolInputException($"there is no group '{from}' (groups: {(groups.Count == 0 ? "none yet" : string.Join(", ", groups))})");
        }

        return ToolResult.Json(new { renamed = from, to = to.Trim(), sessions = moved });
    }

    /// <summary>A slide in a line: what it shows, and the start of what is said over it.</summary>
    private static string Gist(Slide slide)
    {
        var elements = string.Join(", ", slide.Elements.Select(e => e.TypeName).Distinct());
        var narration = slide.Narration is { Length: > 0 } text ? " — " + Gist(CueParser.Parse(text).Text, 120) : "";
        return elements + narration;
    }

    private static string Gist(string text, int length)
    {
        var flat = text.ReplaceLineEndings(" ").Trim();
        return flat.Length <= length ? flat : flat[..(length - 1)].TrimEnd() + "…";
    }

    private ToolResult NewDeck(JsonObject arguments)
    {
        var title = RequireString(arguments, "title");
        var deck = _decks.New(title);
        return ToolResult.Json(new { deck = deck.Id, title = deck.Title, next = "put_slide the first slide now, so the stage is not empty" });
    }

    private ToolResult OpenDeck(JsonObject arguments)
    {
        var id = RequireString(arguments, "id");
        Deck deck;
        try
        {
            deck = _decks.Open(id);
        }
        catch (Exception ex) when (ex is FileNotFoundException or ArgumentException)
        {
            var known = string.Join(", ", _decks.Store.List().Select(d => d.Id));
            throw new ToolInputException($"{ex.Message} Decks: {(known.Length == 0 ? "none yet" : known)}.");
        }

        return ToolResult.Json(new { deck = deck.Id, title = deck.Title, slides = deck.Slides.Select(s => s.Id) });
    }

    private async Task<ToolResult> PutSlideAsync(JsonObject arguments, CancellationToken cancellationToken)
    {
        if (arguments["slide"] is not JsonObject slideJson)
        {
            throw new ToolInputException("slide is required: an object with title, elements and narration");
        }

        var parsed = Deserialize<Slide>(slideJson, "slide");
        var after = OptionalString(arguments, "after");

        var deck = _decks.Ensure(parsed.Title);
        var slide = SpecValidator.WithIds(parsed, id => deck.IndexOf(id) >= 0);
        var issues = SpecValidator.Validate(slide);

        if (SpecValidator.HasErrors(issues))
        {
            return Rejected(issues);
        }

        var (index, replaced) = _decks.PutSlide(slide, after);
        return await ReportAsync(slide.Id, index, replaced, issues, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ToolResult> UpdateSlideAsync(JsonObject arguments, CancellationToken cancellationToken)
    {
        var id = RequireString(arguments, "id");
        var deck = _decks.Current ?? throw new ToolInputException("No deck is open.");
        var current = deck.Find(id) ?? throw new ToolInputException(
            $"There is no slide '{id}' (slides: {string.Join(", ", deck.Slides.Select(s => s.Id))}).");

        var updated = current;

        if (arguments.ContainsKey("title"))
        {
            updated = updated with { Title = OptionalString(arguments, "title") };
        }

        if (arguments.ContainsKey("layout"))
        {
            updated = updated with { Layout = OptionalString(arguments, "layout") };
        }

        if (arguments["size"] is JsonArray size)
        {
            updated = updated with { Size = Deserialize<double[]>(size, "size") };
        }

        if (arguments.ContainsKey("narration"))
        {
            updated = updated with { Narration = OptionalString(arguments, "narration") };
        }

        if (arguments.ContainsKey("advance"))
        {
            updated = updated with { Advance = OptionalString(arguments, "advance") };
        }

        if (arguments.ContainsKey("notes"))
        {
            updated = updated with { Notes = OptionalString(arguments, "notes") };
        }

        var elements = updated.Elements.ToList();

        if (arguments["remove"] is JsonArray remove)
        {
            var ids = remove.Select(r => r?.GetValue<string>()).OfType<string>().ToHashSet(StringComparer.Ordinal);
            var missing = ids.Where(r => elements.All(e => e.Id != r)).ToList();
            if (missing.Count > 0)
            {
                throw new ToolInputException(
                    $"remove: no element {string.Join(", ", missing)} on '{id}' (elements: {string.Join(", ", elements.Select(e => e.Id))})");
            }

            elements.RemoveAll(e => ids.Contains(e.Id));
        }

        if (arguments["elements"] is JsonArray additions)
        {
            var incoming = Deserialize<List<Element>>(additions, "elements");
            foreach (var element in incoming)
            {
                var at = element.Id.Length == 0 ? -1 : elements.FindIndex(e => e.Id == element.Id);
                if (at >= 0)
                {
                    elements[at] = element;
                }
                else
                {
                    elements.Add(element);
                }
            }
        }

        updated = SpecValidator.WithIds(updated with { Elements = elements }, _ => false);
        var issues = SpecValidator.Validate(updated);
        if (SpecValidator.HasErrors(issues))
        {
            return Rejected(issues);
        }

        _decks.UpdateSlide(id, _ => updated);
        var index = _decks.Current!.IndexOf(id);
        return await ReportAsync(id, index, replaced: true, issues, cancellationToken).ConfigureAwait(false);
    }

    private ToolResult RemoveSlide(JsonObject arguments)
    {
        var id = RequireString(arguments, "id");
        _decks.RemoveSlide(id);
        return ToolResult.Json(new { removed = id, slides = _decks.Current!.Slides.Select(s => s.Id) });
    }

    private ToolResult MoveSlide(JsonObject arguments)
    {
        var id = RequireString(arguments, "id");
        var after = RequireString(arguments, "after");
        var index = _decks.MoveSlide(id, after);
        return ToolResult.Json(new { moved = id, n = index + 1, slides = _decks.Current!.Slides.Select(s => s.Id) });
    }

    private async Task<ToolResult> GoToAsync(JsonObject arguments, CancellationToken cancellationToken)
    {
        var deck = _decks.Current ?? throw new ToolInputException("No deck is open.");
        var which = arguments["slide"] switch
        {
            JsonValue value when value.TryGetValue<int>(out var number) => number.ToString(CultureInfo.InvariantCulture),
            JsonValue value => value.ToString(),
            _ => throw new ToolInputException("slide is required: a slide id or a 1-based number"),
        };

        var slide = deck.Find(which);
        if (slide is null && int.TryParse(which, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n >= 1 && n <= deck.Count)
        {
            slide = deck.Slides[n - 1];
        }

        if (slide is null)
        {
            throw new ToolInputException($"There is no slide '{which}' (slides: {string.Join(", ", deck.Slides.Select(s => s.Id))}).");
        }

        var play = Flag(arguments, "play") ?? true;
        await _stage.GoToAsync(slide.Id, play, cancellationToken).ConfigureAwait(false);
        return ToolResult.Json(new { showing = slide.Id, n = deck.IndexOf(slide.Id) + 1, of = deck.Count });
    }

    private async Task<ToolResult> FocusAsync(JsonObject arguments, CancellationToken cancellationToken)
    {
        var target = OptionalString(arguments, "target");
        var region = arguments["region"] is JsonArray array ? Deserialize<double[]>(array, "region") : null;

        if (region is not null && (region.Length != 4 || region.Any(v => v < 0 || v > 1)))
        {
            throw new ToolInputException("region is [x, y, w, h] as fractions within 0..1 of the element");
        }

        if (region is not null && target is null)
        {
            throw new ToolInputException("region needs a target element to zoom within");
        }

        var error = await _stage.FocusAsync(target, region, cancellationToken).ConfigureAwait(false);
        return error is null
            ? ToolResult.Json(new { focus = target ?? "cleared" })
            : ToolResult.Error(error);
    }

    private async Task<ToolResult> SayAsync(JsonObject arguments, CancellationToken cancellationToken)
    {
        var text = RequireString(arguments, "text");
        var parsed = CueParser.Parse(text);
        if (parsed.Errors.Count > 0)
        {
            return ToolResult.Error(string.Join("\n", parsed.Errors));
        }

        await _stage.SayAsync(text, cancellationToken).ConfigureAwait(false);
        return ToolResult.Json(new { saying = parsed.Text });
    }

    private ToolResult Reply(JsonObject arguments)
    {
        _stage.Reply(RequireString(arguments, "text"));
        return ToolResult.Ok("shown in the chat");
    }

    private async Task<ToolResult> WaitForUserAsync(JsonObject arguments, McpSession? session, CancellationToken cancellationToken)
    {
        // The hosted agent receives events as messages on its stdin, and they are only read
        // between turns; waiting here would hold the turn open while the user's next message
        // sat unread behind it.
        if (session?.Kind == SessionKind.Hosted)
        {
            return ToolResult.Ok("You are intrf4ce's built-in presenter: the user's messages and stage events reach you as new messages. Do not wait for them; end your turn when the work is done.");
        }

        var seconds = Number(arguments, "timeout_seconds") ?? 60;
        seconds = Math.Clamp(seconds, 1, MaxWaitSeconds);

        var events = await _events.TakeAsync(TimeSpan.FromSeconds(seconds), cancellationToken).ConfigureAwait(false);
        if (events.Count == 0)
        {
            var state = _stage.State;
            return ToolResult.Json(new
            {
                events = Array.Empty<object>(),
                note = $"Nothing from the user in {seconds:0} s. They are on slide {state.CurrentIndex + 1} of {_decks.Current?.Count ?? 0}"
                    + (state.UserAtNewest ? ", the newest." : "."),
            });
        }

        return ToolResult.Json(new
        {
            events = events.Select(e => new
            {
                type = e.Type,
                text = e.Text,
                meta = e.Meta,
                at = e.At.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
            }),
        });
    }

    private async Task<ToolResult> SnapshotAsync(JsonObject arguments, CancellationToken cancellationToken)
    {
        var slide = OptionalString(arguments, "slide");
        var width = (int)Math.Clamp(Number(arguments, "width") ?? 1280, 320, 2560);

        if (slide is not null && _decks.Current?.Find(slide) is null)
        {
            throw new ToolInputException($"There is no slide '{slide}'.");
        }

        var png = await _stage.SnapshotAsync(slide, width, cancellationToken).ConfigureAwait(false);
        var state = _stage.State;
        var caption = slide is null
            ? $"The stage as the user sees it: slide {state.CurrentIndex + 1} of {_decks.Current?.Count ?? 0}"
                + (state.Focus is null ? "." : $", focused on {state.Focus}.")
            : $"Slide '{slide}' rendered on its own at {Size(slide)} units.";

        return ToolResult.Image(png, caption);
    }

    /// <summary>A slide's size as written in a caption: 1600x900, or a canvas's own.</summary>
    private string Size(string slideId) =>
        _decks.Current?.Find(slideId) is { } slide && SlideLayout.SizeOf(slide) is var (w, h)
            ? string.Create(CultureInfo.InvariantCulture, $"{w:0}x{h:0}")
            : "1600x900";

    private async Task<ToolResult> InspectImageAsync(JsonObject arguments, CancellationToken cancellationToken)
    {
        var source = RequireString(arguments, "src");
        var grid = Flag(arguments, "grid") ?? true;

        try
        {
            var image = await _media.InspectAsync(source, grid, cancellationToken).ConfigureAwait(false);
            var guide = grid
                ? " Grid lines are every 0.1 of the width and height; labels give their value. Callout 'at' is [x, y] in these units."
                : "";
            return ToolResult.Image(image.Png, image.Summary + guide);
        }
        catch (Exception ex) when (ex is FileNotFoundException or HttpRequestException or InvalidDataException)
        {
            return ToolResult.Error($"Could not read {source}: {ex.Message}");
        }
    }

    private async Task<ToolResult> ScanMediaAsync(JsonObject arguments, CancellationToken cancellationToken)
    {
        var folder = RequireString(arguments, "folder");
        var recursive = Flag(arguments, "recursive") ?? false;
        var limit = (int)Math.Clamp(Number(arguments, "limit") ?? 200, 1, 2000);

        if (!Directory.Exists(folder))
        {
            return ToolResult.Error($"There is no folder {folder}.");
        }

        var items = await _media.ScanAsync(folder, recursive, limit, cancellationToken).ConfigureAwait(false);
        return ToolResult.Json(new
        {
            folder,
            count = items.Count,
            images = items.Select(i => new
            {
                path = i.Path,
                size = $"{i.Width}x{i.Height}",
                taken = i.Taken?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                gps = i.Latitude is { } lat && i.Longitude is { } lon ? $"{lat:0.#####}, {lon:0.#####}" : null,
                camera = i.Camera,
            }),
        });
    }

    private async Task<ToolResult> PlaybackAsync(JsonObject arguments, CancellationToken cancellationToken)
    {
        var deck = _decks.Current ?? throw new ToolInputException("No deck is open.");
        var action = OptionalString(arguments, "playback");
        if (action is not (null or "play" or "pause" or "stop"))
        {
            throw new ToolInputException("playback is play, pause or stop");
        }

        foreach (var name in new[] { "from", "through" })
        {
            if (OptionalString(arguments, name) is { } slide && deck.Find(slide) is null)
            {
                throw new ToolInputException(
                    $"{name}: there is no slide '{slide}' (slides: {string.Join(", ", deck.Slides.Select(s => s.Id))}).");
            }
        }

        var request = new PlaybackRequest(
            action,
            OptionalString(arguments, "from"),
            OptionalString(arguments, "through"),
            Flag(arguments, "continuous"),
            Flag(arguments, "muted"),
            Flag(arguments, "subtitles"));

        var now = await _stage.PlaybackAsync(request, cancellationToken).ConfigureAwait(false);
        return ToolResult.Json(new { playback = now });
    }

    private ToolResult GetSettings() =>
        ToolResult.Json(new
        {
            file = _settings.File,
            settings = _settings.Read().Select(s => new
            {
                key = s.Entry.Key,
                value = s.Value,
                is_ = s.Entry.Kind.ToString().ToLowerInvariant(),
                of = s.Entry.Choices,
                between = s.Entry.Kind == SettingKind.Number ? new[] { s.Entry.Minimum, s.Entry.Maximum } : null,
                about = s.Entry.Description,
                restart = s.Entry.NeedsRestart ? true : (bool?)null,
            }),
            note = "These belong to the user. Change one when they ask for it, not to suit yourself.",
        });

    private ToolResult SetSetting(JsonObject arguments)
    {
        var key = RequireString(arguments, "key");
        var value = RequireString(arguments, "value");

        if (_settings.Write(key, value) is { } error)
        {
            return ToolResult.Error(error);
        }

        return ToolResult.Json(new { set = key, to = value, note = "in force now unless it says it needs a restart" });
    }

    private async Task<ToolResult> ReportAsync(
        string slideId, int index, bool replaced, IReadOnlyList<SpecIssue> issues, CancellationToken cancellationToken)
    {
        var report = await _stage.AwaitSlideAsync(slideId, RenderWait, cancellationToken).ConfigureAwait(false);

        var warnings = issues
            .Where(i => i.Severity == IssueSeverity.Warning)
            .Concat(report.Issues)
            .Select(i => i.ToString())
            .Distinct()
            .ToList();

        var state = _stage.State;
        return ToolResult.Json(new
        {
            slide = slideId,
            n = index + 1,
            of = _decks.Current?.Count ?? 0,
            replaced,
            ready = report.Ready,
            problems = warnings,
            parts = report.Parts.Count == 0 ? null : report.Parts,
            user = state.CurrentSlideId is null ? "not looking at a slide yet" : $"on slide {state.CurrentIndex + 1}" + (state.UserAtNewest ? " (the newest)" : ""),
        });
    }

    private static ToolResult Rejected(IReadOnlyList<SpecIssue> issues) =>
        ToolResult.Error("The slide was not changed. Fix these and send it again:\n" + string.Join("\n", issues.Select(i => i.ToString())));

    private static T Deserialize<T>(JsonNode node, string what)
    {
        try
        {
            return node.Deserialize<T>(DeckJson.Options) ?? throw new ToolInputException($"{what} is empty");
        }
        catch (JsonException ex)
        {
            var path = ex.Path is { Length: > 1 } p ? what + p.TrimStart('$') : what;

            // Inside an element, the serializer knows only the element; the rest of the way in is kept beside it.
            if (ex is ElementJsonException { Inner.Length: > 0 } inside)
            {
                path += inside.Inner.StartsWith('[') ? inside.Inner : "." + inside.Inner;
            }

            throw new ToolInputException($"{path}: {Explain(ex)}");
        }
    }

    /// <summary>The serializer's messages, rephrased where they would confuse an agent.</summary>
    private static string Explain(JsonException ex)
    {
        var message = ex.Message;

        if (message.Contains("could not be mapped to any .NET member", StringComparison.Ordinal))
        {
            var start = message.IndexOf('\'', StringComparison.Ordinal);
            var end = start < 0 ? -1 : message.IndexOf('\'', start + 1);
            var property = start >= 0 && end > start ? message[(start + 1)..end] : "a property";
            return $"unknown property '{property}' for this element type; check the spelling and the type's fields in the tool description";
        }

        var cut = message.IndexOf(" Path:", StringComparison.Ordinal);
        return cut > 0 ? message[..cut] : message;
    }

    /// <summary>A number however it arrived: agents send 60, 60.0 and "60" for the same thing.</summary>
    private static double? Number(JsonObject arguments, string name) =>
        arguments[name] switch
        {
            null => null,
            JsonValue value when value.TryGetValue<double>(out var d) => d,
            JsonValue value when value.TryGetValue<int>(out var i) => i,
            JsonValue value when value.TryGetValue<long>(out var l) => l,
            JsonValue value when value.TryGetValue<JsonElement>(out var e) && e.ValueKind == JsonValueKind.Number => e.GetDouble(),
            JsonValue value when value.TryGetValue<string>(out var text)
                && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => throw new ToolInputException($"{name} must be a number"),
        };

    /// <summary>A boolean however it arrived, including "true" as a string.</summary>
    private static bool? Flag(JsonObject arguments, string name) =>
        arguments[name] switch
        {
            null => null,
            JsonValue value when value.TryGetValue<bool>(out var b) => b,
            JsonValue value when value.TryGetValue<JsonElement>(out var e) && e.ValueKind is JsonValueKind.True or JsonValueKind.False => e.GetBoolean(),
            JsonValue value when value.TryGetValue<string>(out var text) && bool.TryParse(text, out var parsed) => parsed,
            _ => throw new ToolInputException($"{name} must be true or false"),
        };

    private static string RequireString(JsonObject arguments, string name) =>
        OptionalString(arguments, name) is { Length: > 0 } value
            ? value
            : throw new ToolInputException($"{name} is required");

    private static string? OptionalString(JsonObject arguments, string name) =>
        arguments[name] switch
        {
            null => null,
            JsonValue value when value.TryGetValue<string>(out var text) => text,
            JsonValue value => value.ToJsonString(),
            _ => throw new ToolInputException($"{name} must be a string"),
        };
}

/// <summary>A tool a plugin answers: which plugin, and how.</summary>
public sealed record PluginToolHandler(string Plugin, Func<JsonObject, CancellationToken, Task<string>> Run);

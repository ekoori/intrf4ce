using Avalonia.Media;
using Intrf4ce.Agent.Mcp;
using Intrf4ce.Agent.Tools;
using Intrf4ce.Core;
using Intrf4ce.Core.Model;
using Intrf4ce.Core.Settings;
using Intrf4ce.Plugins;
using Intrf4ce.Plugins.Hosting;

namespace Intrf4ce.App.Plugins;

/// <summary>A plugin that is loaded, as <c>:plugins</c> lists it.</summary>
/// <param name="Installed">The file in the plugins folder it came from, when it came from there.</param>
public sealed record PluginInfo(
    PluginManifest Manifest,
    string Source,
    bool BuiltIn,
    string? Installed,
    IReadOnlyList<string> Elements,
    IReadOnlyList<string> Tools);

/// <summary>
/// Finds plugins, loads them, and puts what they add where the rest of the app looks for it:
/// element types where the validator and the stage find them, tools and guides where the agent
/// does, prompts on the command line.
/// </summary>
/// <remarks>
/// <para>
/// Three places, first wins for an id: the files settings.yaml names in <c>plugins.load</c>, so
/// that a plugin being worked on is picked up from its build; the <c>plugins</c> folder beside
/// settings.yaml, where <c>:plugin install</c> puts them; and the executable, which carries the
/// built-in ones packed exactly as any other.
/// </para>
/// <para>
/// Loading is once per id per run. .NET cannot unload an assembly while anything made from it is
/// alive, and a slide's controls are; so turning a plugin off, removing it or replacing it waits
/// for the next start, and says so.
/// </para>
/// </remarks>
public sealed class PluginService
{
    /// <summary>The prefix of the resources the built-in plugins are packed into.</summary>
    private const string BuiltInPrefix = "plugins/";

    private readonly AppPaths _paths;
    private readonly AppSettings _settings;
    private readonly HttpClient _http;
    private readonly PluginTheme _theme;
    private ToolCatalog? _catalog;
    private PresenterTools? _tools;
    private readonly Dictionary<string, ElementType> _types = new(StringComparer.Ordinal);
    private readonly List<LoadedPlugin> _loaded = [];
    private readonly List<PluginInfo> _infos = [];
    private readonly List<string> _problems = [];

    public PluginService(AppPaths paths, AppSettings settings, HttpClient http, Skin skin)
    {
        _paths = paths;
        _settings = settings;
        _http = http;
        _theme = Theme(skin);
    }

    /// <summary>
    /// Where what plugins add for the agent goes. Separate from construction because the stage
    /// needs this service before the agent's tools, which need the stage, exist.
    /// </summary>
    public void Connect(ToolCatalog catalog, PresenterTools tools)
    {
        _catalog = catalog;
        _tools = tools;
    }

    /// <summary>Something was loaded: the commands its prompts make need making.</summary>
    public event EventHandler? Changed;

    /// <summary>Where installed plugins live.</summary>
    public string Folder => Path.Combine(_paths.Config, "plugins");

    public IReadOnlyList<PluginInfo> Loaded => _infos;

    /// <summary>What could not be loaded, and why, in words for the user.</summary>
    public IReadOnlyList<string> Problems => _problems;

    /// <summary>Each loaded plugin's ready-made requests.</summary>
    public IEnumerable<(PluginManifest Plugin, PluginPrompt Prompt)> Prompts =>
        _loaded.SelectMany(p => (p.Manifest.Prompts ?? []).Select(prompt => (p.Manifest, prompt)));

    /// <summary>Loads every plugin there is, once, at startup.</summary>
    public void LoadAll()
    {
        foreach (var (source, read, builtIn) in Discover())
        {
            try
            {
                Load(read(), builtIn, installed: !builtIn && IsInFolder(source) ? source : null);
            }
            catch (PluginException ex)
            {
                Problem($"{Path.GetFileName(source)}: {ex.Message}", ex);
            }
        }

        Publish();
    }

    /// <summary>Loads a plugin that was turned off, now that it is on; a message when it cannot be found or is in already.</summary>
    public string LoadNow(string id)
    {
        if (_infos.Any(p => p.Manifest.Id == id))
        {
            return $"{id} is loaded already";
        }

        foreach (var (source, read, builtIn) in Discover())
        {
            PluginPackage package;
            try
            {
                package = read();
            }
            catch (PluginException)
            {
                continue;
            }

            if (package.Manifest.Id == id)
            {
                Load(package, builtIn, installed: !builtIn && IsInFolder(source) ? source : null);
                Publish();
                return $"{package.Manifest.Name} {package.Manifest.Version} is loaded: {Describe(_infos[^1])}";
            }
        }

        throw new InvalidOperationException($"there is no plugin '{id}' to load; :plugins lists them");
    }

    /// <summary>Whether a plugin draws elements of this type.</summary>
    public bool Draws(string kind) => _types.ContainsKey(kind);

    /// <summary>A live element of a plugin's type, for one element on one slide; null when no loaded plugin draws it.</summary>
    public LiveElement? Create(string kind, string slideId, string elementId)
    {
        if (!_types.TryGetValue(kind, out var type))
        {
            return null;
        }

        var services = Services(ElementTypes.Find(kind)?.Plugin ?? kind);
        return type.Create(new ElementHost
        {
            Theme = _theme,
            Http = services.Http,
            Clock = services.Clock,
            Settings = services.Settings,
            Log = services.Log,
            ElementId = elementId,
            SlideId = slideId,
        });
    }

    /// <summary>What one plugin gets: the shared client and clock, its own settings, the log under its name.</summary>
    private PluginServices Services(string plugin) => new()
    {
        Http = _http,
        Clock = TimeProvider.System,
        Settings = () => _settings.Plugins.OptionsFor(plugin),
        Log = line => Log.Write($"plugin {plugin}: {line}"),
    };

    /// <summary>
    /// Copies a plugin file into the plugins folder and loads it, or — when a plugin with its id
    /// is loaded already — leaves it for the next start.
    /// </summary>
    /// <returns>What happened, for the user.</returns>
    public string Install(string path)
    {
        var package = PluginPackage.ReadFile(path);
        var id = package.Manifest.Id;

        Directory.CreateDirectory(Folder);
        var target = Path.Combine(Folder, id + PluginPackage.Extension);
        if (!string.Equals(Path.GetFullPath(path), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
        {
            File.Copy(path, target, overwrite: true);
        }

        if (_infos.FirstOrDefault(p => p.Manifest.Id == id) is { } running)
        {
            return $"{package.Manifest.Name} {package.Manifest.Version} is installed; {running.Manifest.Version} stays in use until intrf4ce starts again";
        }

        if (_settings.Plugins.Disabled.Contains(id))
        {
            return $"{package.Manifest.Name} is installed but turned off: :plugin enable {id}";
        }

        Load(PluginPackage.ReadFile(target), builtIn: false, installed: target);
        Publish();
        var info = _infos.Last();
        return $"{package.Manifest.Name} {package.Manifest.Version} is loaded: {Describe(info)}";
    }

    /// <summary>Takes an installed plugin's file away; it stays in use until the next start.</summary>
    public string Remove(string id)
    {
        var file = Path.Combine(Folder, id + PluginPackage.Extension);
        if (!File.Exists(file))
        {
            return _infos.Any(p => p.Manifest.Id == id && p.BuiltIn)
                ? $"{id} is built in and cannot be removed; :plugin disable {id} turns it off"
                : throw new InvalidOperationException($"there is no installed plugin '{id}'");
        }

        File.Delete(file);
        return $"{id} is removed; it stays in use until intrf4ce starts again";
    }

    /// <summary>The plugin's elements and tools, in a line.</summary>
    public static string Describe(PluginInfo plugin)
    {
        var adds = plugin.Elements.Select(e => $"{e} element").Concat(plugin.Tools.Select(t => $"{t} tool")).ToList();
        return adds.Count == 0 ? "adds nothing" : string.Join(", ", adds);
    }

    private void Load(PluginPackage package, bool builtIn, string? installed)
    {
        var id = package.Manifest.Id;
        if (_infos.Any(p => p.Manifest.Id == id))
        {
            Log.Write($"plugin {id} from {package.Source} is already loaded from elsewhere; skipped");
            return;
        }

        if (_settings.Plugins.Disabled.Contains(id))
        {
            Log.Write($"plugin {id} is turned off in settings.yaml");
            return;
        }

        var loaded = PluginLoader.Load(package, ElementTypes.IsTaken, name => _catalog?.Names.Contains(name) == true, Services(id));
        foreach (var type in loaded.Elements)
        {
            ElementTypes.Add(new ExtraElementType(
                type.Name,
                id,
                type.Description,
                type.Fields,
                type.Required,
                type.IsVisual,
                type.Check is { } check ? fields => check(fields).Select(i => new ExtraElementIssue(i.Field, i.Message, i.IsWarning)) : null));
            _types[type.Name] = type;
        }

        _loaded.Add(loaded);
        _infos.Add(new PluginInfo(
            loaded.Manifest,
            package.Source,
            builtIn,
            installed,
            [.. loaded.Elements.Select(e => e.Name)],
            [.. loaded.Tools.Select(t => t.Name)]));

        Log.Write($"plugin {id} {loaded.Manifest.Version} loaded from {package.Source}: {Describe(_infos[^1])}");
    }

    /// <summary>Tells the agent's side, and the command line, what is loaded now.</summary>
    private void Publish()
    {
        if (_catalog is null || _tools is null)
        {
            throw new InvalidOperationException("the plugin service is not connected to the agent's tools");
        }

        _catalog.SetPlugins([.. _loaded.Select(p => new CatalogPlugin(
            p.Manifest.Id,
            p.Manifest.Name,
            p.Manifest.Version,
            p.Prompt,
            [.. p.Elements.Select(e => ElementTypes.Find(e.Name)).OfType<ExtraElementType>()],
            [.. p.Tools.Select(t => new CatalogTool(t.Name, t.Description, t.InputSchema))]))]);

        _tools.SetPluginTools(_loaded
            .SelectMany(p => p.Tools.Select(t => (Plugin: p.Manifest.Id, Tool: t)))
            .ToDictionary(x => x.Tool.Name, x => new PluginToolHandler(x.Plugin, x.Tool.Run), StringComparer.Ordinal));

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private IEnumerable<(string Source, Func<PluginPackage> Read, bool BuiltIn)> Discover()
    {
        foreach (var path in _settings.Plugins.Load.Where(p => !string.IsNullOrWhiteSpace(p)))
        {
            var full = Environment.ExpandEnvironmentVariables(path.Trim());
            yield return (full, () => PluginPackage.ReadFile(full), false);
        }

        if (Directory.Exists(Folder))
        {
            foreach (var file in Directory.EnumerateFiles(Folder, "*" + PluginPackage.Extension).Order(StringComparer.OrdinalIgnoreCase))
            {
                yield return (file, () => PluginPackage.ReadFile(file), false);
            }
        }

        var assembly = typeof(PluginService).Assembly;
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith(BuiltInPrefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            yield return ($"built in: {name[BuiltInPrefix.Length..]}", () =>
            {
                using var stream = assembly.GetManifestResourceStream(name)!;
                return PluginPackage.Read(stream, "built in");
            }, true);
        }
    }

    private bool IsInFolder(string file) =>
        string.Equals(Path.GetDirectoryName(Path.GetFullPath(file)), Path.GetFullPath(Folder), StringComparison.OrdinalIgnoreCase);

    private void Problem(string message, Exception ex)
    {
        _problems.Add(message);
        Log.Error("plugin " + message, ex);
    }

    private static PluginTheme Theme(Skin skin) => new(
        skin.Ink,
        skin.Surface,
        skin.Surface2,
        skin.Line,
        skin.Text,
        skin.Muted,
        skin.Faint,
        skin.Accent,
        skin.Cool,
        skin.Danger,
        skin.Success,
        [.. skin.Palette.Select(Color.Parse)],
        skin.UiFont,
        new FontFamily("Cascadia Mono, Consolas, Menlo, monospace"),
        FontSize: 28);
}

using System.Reflection;
using System.Runtime.Loader;
using System.Text.RegularExpressions;

namespace Intrf4ce.Plugins.Hosting;

/// <summary>A plugin whose code has run and said what it adds.</summary>
public sealed record LoadedPlugin(
    PluginManifest Manifest,
    string? Prompt,
    string Source,
    IReadOnlyList<ElementType> Elements,
    IReadOnlyList<PluginTool> Tools);

/// <summary>Loads a package's code and asks its plugin what it adds.</summary>
public static partial class PluginLoader
{
    [GeneratedRegex("^[a-z][a-z0-9-]{0,31}$")]
    private static partial Regex ElementName();

    [GeneratedRegex("^[a-zA-Z0-9_-]{1,64}$")]
    private static partial Regex ToolName();

    /// <param name="elementTaken">Whether an element type name is already somebody's: intrf4ce's or another plugin's.</param>
    /// <param name="toolTaken">The same, for tool names.</param>
    /// <param name="services">What the plugin gets to work with.</param>
    /// <exception cref="PluginException">The code would not load, or registered something it may not.</exception>
    public static LoadedPlugin Load(PluginPackage package, Func<string, bool> elementTaken, Func<string, bool> toolTaken, PluginServices services)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(elementTaken);
        ArgumentNullException.ThrowIfNull(toolTaken);
        ArgumentNullException.ThrowIfNull(services);

        var id = package.Manifest.Id;
        var context = new PluginLoadContext(package);

        Type entry;
        try
        {
            var assembly = context.LoadEntry();
            var plugins = assembly.GetExportedTypes()
                .Where(t => typeof(IPlugin).IsAssignableFrom(t) && t is { IsAbstract: false, IsInterface: false }
                    && t.GetConstructor(Type.EmptyTypes) is not null)
                .ToList();

            entry = plugins.Count == 1
                ? plugins[0]
                : throw new PluginException(plugins.Count == 0
                    ? $"{id}: {package.Manifest.Entry} has no public class implementing IPlugin with a constructor that takes nothing"
                    : $"{id}: {package.Manifest.Entry} has {plugins.Count} IPlugin classes; it needs exactly one");
        }
        catch (Exception ex) when (ex is BadImageFormatException or FileLoadException or FileNotFoundException
            or TypeLoadException or ReflectionTypeLoadException)
        {
            throw new PluginException($"{id}'s code would not load: {ex.Message}", ex);
        }

        var registrar = new Registrar(id, elementTaken, toolTaken, services);
        try
        {
            ((IPlugin)Activator.CreateInstance(entry)!).Register(registrar);
        }
        catch (PluginException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Somebody else's code: whatever it throws becomes a plugin that did not load,
            // not an app that did not start.
            var cause = ex is TargetInvocationException { InnerException: { } inner } ? inner : ex;
            throw new PluginException($"{id} failed while starting: {cause.Message}", cause);
        }

        return new LoadedPlugin(package.Manifest, package.Prompt, package.Source, registrar.Elements, registrar.Tools);
    }

    private sealed class Registrar(string id, Func<string, bool> elementTaken, Func<string, bool> toolTaken, PluginServices services) : IPluginRegistrar
    {
        public string PluginId => id;

        public PluginServices Services => services;

        public List<ElementType> Elements { get; } = [];

        public List<PluginTool> Tools { get; } = [];

        public void AddElement(ElementType type)
        {
            ArgumentNullException.ThrowIfNull(type);

            if (type.Name is null || !ElementName().IsMatch(type.Name))
            {
                throw new PluginException($"{id}: element type '{type.Name}' must be lower case letters, digits and '-'");
            }

            if (elementTaken(type.Name) || Elements.Any(e => e.Name == type.Name))
            {
                throw new PluginException($"{id}: there is already an element type called '{type.Name}'");
            }

            Elements.Add(type);
        }

        public void AddTool(PluginTool tool)
        {
            ArgumentNullException.ThrowIfNull(tool);

            // The id first, so that two plugins cannot both have a tool called "now".
            if (tool.Name is null || !ToolName().IsMatch(tool.Name) || !tool.Name.StartsWith(id + "_", StringComparison.Ordinal))
            {
                throw new PluginException($"{id}: tool '{tool.Name}' must be named {id}_something");
            }

            if (toolTaken(tool.Name) || Tools.Any(t => t.Name == tool.Name))
            {
                throw new PluginException($"{id}: there is already a tool called '{tool.Name}'");
            }

            Tools.Add(tool);
        }
    }

    /// <summary>
    /// Where a plugin's assemblies live: a context of their own, which takes the app's copy of
    /// anything the app has.
    /// </summary>
    /// <remarks>
    /// The app's Avalonia, the app's <c>Intrf4ce.Plugins</c>: a plugin with its own copy of
    /// either would make controls the stage cannot hold and an IPlugin the app does not know.
    /// Only what the app lacks is loaded from the package. Never unloaded — a .NET assembly can
    /// only be unloaded with everything that touched it, and a slide's controls have.
    /// </remarks>
    private sealed class PluginLoadContext(PluginPackage package)
        : AssemblyLoadContext($"plugin {package.Manifest.Id}", isCollectible: false)
    {
        public Assembly LoadEntry() =>
            FromPackage(package.Manifest.Entry)
            ?? throw new PluginException($"{package.Manifest.Id}: {package.Manifest.Entry} is missing");

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            try
            {
                return Default.LoadFromAssemblyName(assemblyName);
            }
            catch (FileNotFoundException)
            {
                // Not one of the app's: the package's own, if it brought it.
            }
            catch (FileLoadException ex)
            {
                // The app has it, but older than the plugin was built against.
                throw new PluginException(
                    $"{package.Manifest.Id} needs {assemblyName.Name} {assemblyName.Version}, newer than this intrf4ce has: {ex.Message}", ex);
            }

            return assemblyName.Name is { } name ? FromPackage(name + ".dll") : null;
        }

        private Assembly? FromPackage(string file)
        {
            if (!package.Files.TryGetValue(file, out var image))
            {
                return null;
            }

            // The symbols too when they came along, so a plugin's stack traces have line numbers.
            var symbols = package.Files.TryGetValue(Path.ChangeExtension(file, ".pdb"), out var pdb) ? new MemoryStream(pdb) : null;
            using (symbols)
            {
                return LoadFromStream(new MemoryStream(image), symbols);
            }
        }
    }
}

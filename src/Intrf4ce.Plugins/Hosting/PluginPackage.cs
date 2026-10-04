using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Intrf4ce.Plugins.Hosting;

/// <summary>What plugin.json says about a plugin.</summary>
/// <param name="Entry">The assembly in the package holding the <see cref="IPlugin"/> class.</param>
/// <param name="Prompts">Ready-made requests the user can make of the agent; each becomes a command.</param>
public sealed record PluginManifest(
    string Id,
    string Name,
    string Version,
    string Description,
    string Entry,
    int Api = 1,
    string? Author = null,
    IReadOnlyList<PluginPrompt>? Prompts = null);

/// <summary>A request the user can make with one command: <c>:weather Zagreb</c>.</summary>
/// <param name="Text">What is said to the agent; <c>{args}</c> stands for whatever was typed after the command.</param>
/// <param name="Default">What <c>{args}</c> becomes when nothing was typed after it.</param>
public sealed record PluginPrompt(string Name, string Description, string Text, string? Default = null)
{
    /// <summary>The request as said to the agent, with what the user typed in it.</summary>
    public string Said(string? typed) =>
        Text.Replace("{args}", string.IsNullOrWhiteSpace(typed) ? Default ?? "" : typed.Trim(), StringComparison.Ordinal).Trim();
}

/// <summary>
/// A plugin file, <c>name.i4p</c>: a zip holding plugin.json, prompt.md and the plugin's
/// assemblies. Reading one checks it is whole and makes sense before any of its code runs.
/// </summary>
public sealed partial class PluginPackage
{
    public const string Extension = ".i4p";

    public const string ManifestFile = "plugin.json";

    public const string PromptFile = "prompt.md";

    /// <summary>The contract this build of intrf4ce speaks. A plugin asks for one in plugin.json.</summary>
    public const int Api = 1;

    // A package is read into memory whole, so its size is bounded: a zip that inflates to
    // gigabytes is refused rather than allowed to fill memory.
    private const long MaxFile = 64L * 1024 * 1024;
    private const long MaxTotal = 256L * 1024 * 1024;

    /// <summary>Assemblies the app always brings, so a package need not carry them and does not.</summary>
    private static readonly string[] HostAssemblies = ["Avalonia", "Intrf4ce.Plugins", "SkiaSharp", "HarfBuzzSharp", "MicroCom"];

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private PluginPackage(PluginManifest manifest, string? prompt, IReadOnlyDictionary<string, byte[]> files, string source)
    {
        Manifest = manifest;
        Prompt = prompt;
        Files = files;
        Source = source;
    }

    public PluginManifest Manifest { get; }

    /// <summary>prompt.md: how the agent should use what the plugin adds. Null when there is none.</summary>
    public string? Prompt { get; }

    /// <summary>Every file in it, by its path inside the package with '/' between folders.</summary>
    public IReadOnlyDictionary<string, byte[]> Files { get; }

    /// <summary>Where it was read from, for messages: a path, or "built in".</summary>
    public string Source { get; }

    [GeneratedRegex("^[a-z][a-z0-9-]{0,31}$")]
    public static partial Regex IdPattern();

    public static PluginPackage ReadFile(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        try
        {
            using var stream = File.OpenRead(path);
            return Read(stream, path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new PluginException($"could not read {path}: {ex.Message}", ex);
        }
    }

    /// <exception cref="PluginException">It is not a plugin, or not one this intrf4ce can load.</exception>
    public static PluginPackage Read(Stream stream, string source)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            long total = 0;
            foreach (var entry in zip.Entries)
            {
                var name = entry.FullName.Replace('\\', '/');
                if (name.EndsWith('/'))
                {
                    continue;
                }

                if (name.StartsWith('/') || name.Split('/').Contains(".."))
                {
                    throw new PluginException($"{source} has a file that points outside it: {name}");
                }

                total += entry.Length;
                if (entry.Length > MaxFile || total > MaxTotal)
                {
                    throw new PluginException($"{source} is too big to be a plugin ({total / (1024 * 1024)} MB unpacked)");
                }

                using var content = entry.Open();
                using var copy = new MemoryStream((int)entry.Length);
                content.CopyTo(copy);
                files[name] = copy.ToArray();
            }
        }
        catch (InvalidDataException ex)
        {
            throw new PluginException($"{source} is not a plugin file: {ex.Message}", ex);
        }

        if (!files.TryGetValue(ManifestFile, out var manifestBytes))
        {
            throw new PluginException($"{source} has no {ManifestFile}, so it is not a plugin");
        }

        PluginManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<PluginManifest>(manifestBytes, Json)
                ?? throw new PluginException($"{source}: {ManifestFile} is empty");
        }
        catch (JsonException ex)
        {
            throw new PluginException($"{source}: {ManifestFile}: {ex.Message}", ex);
        }

        manifest = Check(manifest, files.Keys, source);

        var prompt = files.TryGetValue(PromptFile, out var promptBytes) ? Encoding.UTF8.GetString(promptBytes).Trim() : null;
        return new PluginPackage(manifest, string.IsNullOrEmpty(prompt) ? null : prompt, files, source);
    }

    /// <summary>
    /// Makes a plugin file from a folder: its plugin.json, its prompt.md, and its assemblies —
    /// a build's output folder will do, as what the app brings anyway is left out.
    /// </summary>
    /// <returns>The manifest it was made from.</returns>
    /// <exception cref="PluginException">The folder does not hold a plugin.</exception>
    public static PluginManifest Pack(string folder, string output)
    {
        ArgumentException.ThrowIfNullOrEmpty(folder);
        ArgumentException.ThrowIfNullOrEmpty(output);

        var manifestPath = Path.Combine(folder, ManifestFile);
        if (!File.Exists(manifestPath))
        {
            throw new PluginException($"{folder} has no {ManifestFile}");
        }

        PluginManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<PluginManifest>(File.ReadAllBytes(manifestPath), Json)
                ?? throw new PluginException($"{manifestPath} is empty");
        }
        catch (JsonException ex)
        {
            throw new PluginException($"{manifestPath}: {ex.Message}", ex);
        }

        var files = Directory.EnumerateFiles(folder)
            .Where(f => Path.GetFileName(f) is ManifestFile or PromptFile
                || (Path.GetExtension(f) is ".dll" or ".pdb" && !IsHostAssembly(Path.GetFileNameWithoutExtension(f))))
            .ToList();

        manifest = Check(manifest, files.Select(f => Path.GetFileName(f)), manifestPath);

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        var temporary = output + ".tmp";
        using (var zip = ZipFile.Open(temporary, ZipArchiveMode.Create))
        {
            foreach (var file in files)
            {
                zip.CreateEntryFromFile(file, Path.GetFileName(file), CompressionLevel.Optimal);
            }
        }

        File.Move(temporary, output, overwrite: true);
        return manifest;
    }

    /// <summary>
    /// Packs a folder into <paramref name="output"/>, or when that is not given into
    /// <c>id.i4p</c> in the folder itself, named after the plugin as an installed one is.
    /// </summary>
    /// <returns>The manifest, and where the file is.</returns>
    public static (PluginManifest Manifest, string File) PackFolder(string folder, string? output = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(folder);

        folder = Path.GetFullPath(folder);
        if (output is { Length: > 0 })
        {
            var to = Path.GetFullPath(output);
            return (Pack(folder, to), to);
        }

        var first = Path.Combine(folder, "plugin" + Extension);
        var manifest = Pack(folder, first);
        var named = Path.Combine(folder, manifest.Id + Extension);
        File.Move(first, named, overwrite: true);
        return (manifest, named);
    }

    private static bool IsHostAssembly(string name) =>
        HostAssemblies.Any(h => name.Equals(h, StringComparison.OrdinalIgnoreCase)
            || name.StartsWith(h + ".", StringComparison.OrdinalIgnoreCase));

    /// <returns>The manifest with a name and description however it was written.</returns>
    private static PluginManifest Check(PluginManifest manifest, IEnumerable<string> files, string source)
    {
        if (manifest.Id is null || !IdPattern().IsMatch(manifest.Id))
        {
            throw new PluginException($"{source}: id '{manifest.Id}' must be lower case letters, digits and '-', starting with a letter");
        }

        if (string.IsNullOrWhiteSpace(manifest.Version))
        {
            throw new PluginException($"{source}: {manifest.Id} has no version");
        }

        if (manifest.Api != Api)
        {
            throw new PluginException(
                $"{manifest.Id} {manifest.Version} was made for version {manifest.Api} of intrf4ce's plugin contract; this intrf4ce speaks version {Api}");
        }

        if (string.IsNullOrWhiteSpace(manifest.Entry) || !files.Contains(manifest.Entry, StringComparer.OrdinalIgnoreCase))
        {
            throw new PluginException($"{source}: {manifest.Id}'s entry assembly '{manifest.Entry}' is not in the package");
        }

        foreach (var prompt in manifest.Prompts ?? [])
        {
            if (prompt.Name is null || !IdPattern().IsMatch(prompt.Name) || string.IsNullOrWhiteSpace(prompt.Text))
            {
                throw new PluginException($"{source}: {manifest.Id}'s prompt '{prompt.Name}' needs a lower case name and some text");
            }
        }

        return manifest with
        {
            Name = string.IsNullOrWhiteSpace(manifest.Name) ? manifest.Id : manifest.Name.Trim(),
            Description = manifest.Description?.Trim() ?? "",
            Prompts = manifest.Prompts ?? [],
        };
    }
}

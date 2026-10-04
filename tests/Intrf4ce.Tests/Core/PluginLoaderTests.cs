using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Intrf4ce.Plugins;
using Intrf4ce.Plugins.Hosting;

namespace Intrf4ce.Tests.Core;

/// <summary>
/// A plugin file is checked before any of its code runs, loads into a context of its own that
/// shares the app's contract, and says what it adds.
/// </summary>
public sealed class PluginLoaderTests : IDisposable
{
    private static readonly string Packed = Path.Combine(AppContext.BaseDirectory, "plugins", "timezones.i4p");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "intrf4ce-loader-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task TheBuiltInTimezonesPluginLoadsFromItsFileAndSaysWhatItAdds()
    {
        var package = PluginPackage.ReadFile(Packed);

        Assert.Equal("timezones", package.Manifest.Id);
        Assert.Contains("timezones", package.Prompt, StringComparison.Ordinal);
        Assert.Equal("time", Assert.Single(package.Manifest.Prompts!).Name);

        var plugin = PluginLoader.Load(package, _ => false, _ => false, Services());

        var element = Assert.Single(plugin.Elements);
        Assert.Equal("timezones", element.Name);
        Assert.NotNull(element.Fields["zones"]);

        // Its check is the plugin's own code, run from the context it was loaded into.
        var issues = element.Check!(JsonSerializer.SerializeToElement(new { zones = new[] { "Mars/Olympus" } })).ToList();
        Assert.Equal("zones[0]", Assert.Single(issues).Field);

        var tool = Assert.Single(plugin.Tools);
        var answer = await tool.Run(new JsonObject { ["zones"] = new JsonArray("Europe/Zagreb", "Asia/Tokyo") }, CancellationToken.None);
        Assert.Contains("Zagreb:", answer, StringComparison.Ordinal);
        Assert.Contains("Tokyo:", answer, StringComparison.Ordinal);
    }

    [Fact]
    public void APluginMayNotTakeANameThatIsAlreadySomebodys()
    {
        var package = PluginPackage.ReadFile(Packed);

        var ex = Assert.Throws<PluginException>(() => PluginLoader.Load(package, name => name == "timezones", _ => false, Services()));

        Assert.Contains("already an element type called 'timezones'", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, "has no plugin.json")]
    [InlineData("""{"id":"Clock","name":"x","version":"1","description":"","entry":"x.dll"}""", "lower case")]
    [InlineData("""{"id":"clock","name":"x","version":"1","description":"","entry":"x.dll","api":2}""", "version 2 of intrf4ce's plugin contract")]
    [InlineData("""{"id":"clock","name":"x","version":"1","description":"","entry":"missing.dll"}""", "'missing.dll' is not in the package")]
    [InlineData("""{"id":"clock","name":"x","version":"","description":"","entry":"x.dll"}""", "has no version")]
    public void APackageIsCheckedBeforeAnyOfItsCodeRuns(string? manifest, string complaint)
    {
        using var zip = new MemoryStream();
        using (var archive = new ZipArchive(zip, ZipArchiveMode.Create, leaveOpen: true))
        {
            if (manifest is not null)
            {
                Write(archive, PluginPackage.ManifestFile, manifest);
            }

            Write(archive, "x.dll", "not really an assembly");
        }

        zip.Position = 0;
        var ex = Assert.Throws<PluginException>(() => PluginPackage.Read(zip, "test.i4p"));

        Assert.Contains(complaint, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AFileThatIsNotAZipIsSaidToBeNoPlugin()
    {
        using var junk = new MemoryStream(Encoding.UTF8.GetBytes("hello"));

        var ex = Assert.Throws<PluginException>(() => PluginPackage.Read(junk, "hello.i4p"));

        Assert.Contains("not a plugin file", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PackingABuildFolderTakesOnlyThePluginsOwnFilesAndNamesItForThePlugin()
    {
        // A build output folder: the plugin, what the app brings anyway, and things nobody needs.
        Directory.CreateDirectory(_root);
        using (var package = ZipFile.OpenRead(Packed))
        {
            package.ExtractToDirectory(_root);
        }

        File.WriteAllText(Path.Combine(_root, "Avalonia.Base.dll"), "the app has this");
        File.WriteAllText(Path.Combine(_root, "Intrf4ce.Plugins.dll"), "and this");
        File.WriteAllText(Path.Combine(_root, "notes.txt"), "and nobody wants this");

        var (manifest, file) = PluginPackage.PackFolder(_root);

        Assert.Equal("timezones", manifest.Id);
        Assert.Equal(Path.Combine(_root, "timezones.i4p"), file);

        using var packed = ZipFile.OpenRead(file);
        var names = packed.Entries.Select(e => e.FullName).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(["Intrf4ce.Plugin.Timezones.dll", "Intrf4ce.Plugin.Timezones.pdb", "plugin.json", "prompt.md"], names);

        // And it is a plugin that reads back.
        Assert.Equal("timezones", PluginPackage.ReadFile(file).Manifest.Id);
    }

    [Fact]
    public void APromptSaysWhatWasTypedOrItsDefault()
    {
        var prompt = new PluginPrompt("weather", "The weather", "Show the weather in {args}.", "where I am");

        Assert.Equal("Show the weather in Zagreb.", prompt.Said(" Zagreb "));
        Assert.Equal("Show the weather in where I am.", prompt.Said(null));
    }

    private static PluginServices Services() => new()
    {
        Http = new HttpClient(),
        Clock = TimeProvider.System,
        Settings = () => JsonSerializer.SerializeToElement(new { }),
        Log = _ => { },
    };

    private static void Write(ZipArchive archive, string name, string content)
    {
        using var stream = archive.CreateEntry(name).Open();
        stream.Write(Encoding.UTF8.GetBytes(content));
    }
}

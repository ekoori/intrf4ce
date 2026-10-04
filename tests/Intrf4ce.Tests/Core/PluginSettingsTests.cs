using System.Text.Json;
using Intrf4ce.Core.Settings;

namespace Intrf4ce.Tests.Core;

/// <summary>The plugins section of settings.yaml: which are off, and what each falls back on.</summary>
public sealed class PluginSettingsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "intrf4ce-plugin-settings-" + Guid.NewGuid().ToString("N"));

    private string File => Path.Combine(_root, "settings.yaml");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void TurningAPluginOffWritesOneLineAndLeavesTheRestAlone()
    {
        Directory.CreateDirectory(_root);
        System.IO.File.WriteAllText(File, SettingsFile.DefaultContent);

        Assert.Null(SettingsFile.WriteDisabledPlugins(File, ["weather", "holidays"]));

        var text = System.IO.File.ReadAllText(File);
        Assert.Contains("  disabled: [weather, holidays]", text, StringComparison.Ordinal);
        Assert.Contains("# ids not to load", text, StringComparison.Ordinal);
        Assert.Equal(["weather", "holidays"], SettingsFile.Load(File).Settings.Plugins.Disabled);
    }

    [Fact]
    public void AFileWrittenBeforePluginsExistedGetsTheSection()
    {
        Directory.CreateDirectory(_root);
        System.IO.File.WriteAllText(File, "stage:\n  show_controls: true\n");

        Assert.Null(SettingsFile.WriteDisabledPlugins(File, ["weather"]));

        Assert.Equal(["weather"], SettingsFile.Load(File).Settings.Plugins.Disabled);
        Assert.True(SettingsFile.Load(File).Settings.Stage.ShowControls);
    }

    [Fact]
    public void APluginsOptionsReachItAsJsonWithNumbersAndFlagsAsSuch()
    {
        Directory.CreateDirectory(_root);
        System.IO.File.WriteAllText(File, """
            plugins:
              options:
                weather: { place: Zagreb, latitude: 45.81, days: 3, imperial: false }
                timezones:
                  zones: [Australia/Melbourne, Europe/Zagreb]
            """);

        var load = SettingsFile.Load(File);
        var weather = load.Settings.Plugins.OptionsFor("weather");
        var zones = load.Settings.Plugins.OptionsFor("timezones");

        Assert.Empty(load.Warnings ?? []);
        Assert.Equal("Zagreb", weather.GetProperty("place").GetString());
        Assert.Equal(45.81, weather.GetProperty("latitude").GetDouble());
        Assert.Equal(JsonValueKind.False, weather.GetProperty("imperial").ValueKind);
        Assert.Equal("Europe/Zagreb", zones.GetProperty("zones")[1].GetString());
        Assert.Equal(JsonValueKind.Object, load.Settings.Plugins.OptionsFor("nobody").ValueKind);
    }
}

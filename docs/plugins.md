# Writing a plugin

A plugin adds things the agent can put on a slide, keeps them current, gives the agent tools,
and tells it how to use them — all in one file, `id.i4p`. The three built-in plugins in
[`plugins/`](../plugins) are written exactly this way and are the best examples: `Timezones` is
the smallest, `Weather` fetches and caches, `Holidays` lists and scales.

## The file

An `.i4p` is a zip:

```
plugin.json     who it is, which assembly to load, ready-made requests
prompt.md       how the agent should use what it adds
*.dll, *.pdb    its code: the entry assembly, and anything it uses that intrf4ce does not have
```

`plugin.json`:

```json
{
  "id": "weather",
  "name": "Weather",
  "version": "1.0.0",
  "description": "The weather anywhere, kept current while the slide is up.",
  "author": "you",
  "api": 1,
  "entry": "Intrf4ce.Plugin.Weather.dll",
  "prompts": [
    {
      "name": "weather",
      "description": "The weather somewhere, on the stage",
      "text": "Put the weather for {args} on a slide, and tell me what it is doing.",
      "default": "where I am"
    }
  ]
}
```

- `id`: lower case letters, digits and `-`. Its tools' names must begin with it and `_`.
- `api`: the version of the contract it was built for. This intrf4ce speaks `1`.
- `prompts` become commands: `:weather Zagreb` says the `text` to the agent with `{args}` replaced
  by what was typed, or by `default` when nothing was.

`prompt.md` is appended to the agent's instructions under the plugin's name. Say what each
element type is for, its fields, its parts, and when to read it back. The built-in ones are a
paragraph and a list; the agent reads every word, so keep it that short.

## The project

A class library against `Intrf4ce.Plugins` and Avalonia, neither of which goes in the plugin's
output — intrf4ce brings both, and a plugin with its own copy would make controls the stage
cannot hold:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <CopyLocalLockFileAssemblies>false</CopyLocalLockFileAssemblies>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Avalonia" Version="12.0.5" PrivateAssets="all" ExcludeAssets="runtime" />
    <Reference Include="Intrf4ce.Plugins" HintPath="path\to\Intrf4ce.Plugins.dll" Private="false" />
  </ItemGroup>
</Project>
```

Build it, then pack the output folder with plugin.json and prompt.md beside the assembly:

```bash
intrf4ce --pack-plugin bin/Release/net10.0
```

which writes `bin/Release/net10.0/<id>.i4p` with only what belongs in it. While working on one,
`plugins.load: ['C:\path\to\bin\Debug\net10.0\id.i4p']` in settings.yaml picks up each build
without installing it — restart to load a new build.

## The code

One public class implements `IPlugin`, with a constructor that takes nothing:

```csharp
public sealed class WeatherPlugin : IPlugin
{
    public void Register(IPluginRegistrar registrar)
    {
        registrar.AddElement(new ElementType("weather", "The weather somewhere, kept current.", host => new WeatherElement(host))
        {
            Fields = new JsonObject
            {
                ["place"] = new JsonObject { ["type"] = "string", ["description"] = "Where, by name." },
            },
            Required = [],
            Check = fields => /* FieldIssue for anything wrong */ [],
        });

        registrar.AddTool(new PluginTool("weather_now", "The weather somewhere, in words.", schema,
            async (arguments, cancellation) => await AnswerAsync(registrar.Services, arguments, cancellation)));
    }
}
```

- **`Fields`** are JSON Schema property definitions. The agent is shown them in the `element`
  schema, each description after the type's name, and nothing else is accepted: an unknown
  field, or a missing `Required` one, is an error the agent is told about before the slide is
  taken. `Check` says anything more — a zone that does not exist, a number out of range — as
  `FieldIssue`s, warnings or errors, by field.
- **`registrar.Services`** is what the plugin shares with intrf4ce: one `HttpClient` (do not
  dispose it), a `TimeProvider`, the plugin's settings (`plugins.options.<id>` as a JSON object,
  read through a function because the user can change them while it runs), and the log.
- **Tools** answer with text — JSON or words. Throw `ArgumentException` for arguments that are
  wrong, with a message saying how; anything else thrown is reported as the plugin failing.

### A live element

```csharp
internal sealed class WeatherElement(ElementHost host) : LiveElement
{
    public override Control View => _view;

    public override async Task ShowAsync(JsonElement fields, CancellationToken cancellation) { /* fetch, draw, start a timer */ }

    public override string Describe() => /* what it shows now, in words, for read_element */;

    public override IReadOnlyList<string> Parts => ["now", "today", "sat"];
    public override Rect? PartBounds(string part) => /* where, in View's coordinates */;
    public override void Highlight(string? part) { /* mark it */ }

    protected override void Dispose(bool disposing) => _timer.Stop();
}
```

- **On the UI thread.** It is made, shown and disposed on the UI thread and only ever touched
  there. A `DispatcherTimer` ticks on it; a fetch awaited without `ConfigureAwait(false)` comes
  back to it.
- **In slide units.** A slide is 1600 wide; the stage's camera scales everything. Size type from
  `host.Theme.FontSize` (body text, 28) and use the theme's colours — `Text`, `Muted`, `Accent`
  for "look here", `Cool` for the secondary — so the element belongs on the slide.
- **`ShowAsync`** is called once to begin and again whenever the agent changes the element: take
  the new fields without rebuilding what is already there. An exception shows its message in
  the element's place, for the user and the agent.
- **Keeping current** is the element's own business: a clock on the minute, a forecast every
  quarter of an hour. Several slides may show the same thing; share fetches through a cache
  rather than asking a free service five times. Keep showing the last good data when a refresh
  fails, and say how old it is.
- **`Describe`** is what the agent reads with `read_element` before narrating it, so the voice and
  the slide agree. Say it plainly, as of when.
- **`Parts`** are what follows the dot in a focus cue — `[[focus wx.sat]]` — and must be
  letters, digits, `-` and `_`. The stage lights `PartBounds`; `Highlight` is for anything more.
- **`Dispose`** comes when the slide goes. Stop timers there.

## What a plugin may do

It is code, with intrf4ce's permissions: it can read files and reach the network. That is why
`:plugin install` is something the user does, and why the built-in plugins carry nothing but
their own assembly. A plugin cannot be unloaded from a running .NET process, so turning one off
or replacing it waits for the next start, and intrf4ce says so.

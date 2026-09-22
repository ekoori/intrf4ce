# intrf4ce

A presentation that builds itself while you watch.

intrf4ce is a stage an agent drives over MCP. You ask for something to be explained — a concept,
a machine, a folder of photos, a book — and slides appear one after another: images with labelled
callouts, diagrams that zoom node by node, formulas, charts, tables, photo galleries and quizzes,
narrated aloud with subtitles, the camera flying to whatever the narration is talking about.

The agent says *what*; intrf4ce decides *how*. Fonts, colours, motion, voice and pacing are yours,
in `settings.yaml`, and no tool lets an agent touch them.

## Quick start

Download or build a single executable (see [Building](#building)), then run it.

By default intrf4ce runs **Claude Code for you**: start the app, type what you want explained in
the box on the right, and watch. It needs `claude` on your PATH; anything else is settings.

To drive it from an agent you run yourself, set `agent.mode: attached` in `settings.yaml` and
register intrf4ce as an MCP server:

```bash
claude mcp add intrf4ce -- "C:\path\to\intrf4ce.exe" --mcp
```

`intrf4ce --mcp` is the same executable in shim mode: it pumps JSON-RPC between your agent and the
running window, and starts the window if it is not open yet.

## What the agent can put on a slide

| element | what it is |
|---|---|
| `title` | a title slide: kicker, title, subtitle |
| `markdown` | CommonMark with tables, code and `$…$` / `$$…$$` TeX |
| `image` | a file or URL, with callouts laid out in gutters beside it, anatomy-atlas style |
| `diagram` | Mermaid; node ids become focus targets, `steps` lets you walk them |
| `math` | TeX, rendered large |
| `chart` | bar, line, area, pie, donut, scatter — or a full Vega-Lite spec |
| `table` | numbers aligned and grouped, headings that sort, rows you can focus |
| `gallery` | a folder of photos or a list of items: a scattered pile, a grid, or a timeline by date taken |
| `quiz` | single or multiple choice; the answer goes back to the agent |

Narration carries cues that fire as the voice reaches them: `[[focus knee.acl]]` zooms to a callout
and dims the rest, `[[zoom map 0.2 0.3 0.4 0.3]]` frames a region, `[[show chart]]` reveals a hidden
element, `[[clear]]` returns to the whole slide.

## Keys

| | |
|---|---|
| `→` `←` `PgDn` `PgUp` | next, previous slide |
| `↑` `↓` | step through the parts of a slide (callouts, diagram steps, rows) |
| `Home` `End` | first, last slide |
| `O` | overview of the whole deck |
| `L` | sessions: every presentation on disk |
| `T` | show or hide the conversation panel |
| `Space` | play or pause the narration |
| `X` | stop: no voice, and no slide moves on by itself |
| `M` `S` | mute, subtitles |
| `Enter` or `/` | write to the agent |
| `+` `-` `0` | zoom in, out, back to the slide |
| `F` `F11` | fullscreen |
| `Esc` | back: unfocus, leave overview, leave fullscreen |

With the mouse: the wheel zooms, dragging pans, clicking a callout, a node, a photo or a row
focuses it (and again goes back), double-clicking reframes the slide, and right-clicking anything
offers **Ask about this**, which sends your question to the agent along with what you pointed at.
The buttons in the bottom-left corner of the stage do the same as the keys: previous, play or
pause, stop, next. Stop means stopped — the voice goes quiet and nothing advances until you
press play, however much the agent adds in the meantime.

## Settings

`settings.yaml` is written on first run, with every setting and what it does. It lives in:

| | |
|---|---|
| Windows | `%APPDATA%\intrf4ce\settings.yaml` |
| macOS | `~/Library/Application Support/intrf4ce/settings.yaml` |
| Linux | `$XDG_CONFIG_HOME/intrf4ce/settings.yaml` (or `~/.config/intrf4ce`) |

**Sessions.** Each presentation is a folder under `sessions`, beside the executable, holding its
`deck.json` and every image it uses — pictures from the web and files from your own disk are both
copied in — so the folder can be moved to another machine and still present. They are listed on
the left (`L`): open one and it comes back where it was, with the agent returned to the
conversation that built it, free to carry on or rewrite it. If the folder beside the executable
cannot be written (installed under Program Files, say), sessions fall back to `Documents/intrf4ce`,
and decks made before this lived there are moved across on first run. Voice clips, renders and
logs are cache (`%LOCALAPPDATA%\intrf4ce` on Windows) and can be deleted at any time.
`INTRF4CE_HOME=<folder>` puts config, cache and sessions under one folder, which is how the tests
run a second instance beside a real one.

Saving the file applies what can change immediately — voice, pacing, volume, subtitles, animation
speed. Changing the agent or the engine tells you it needs a restart.

**ElevenLabs.** With a key, narration is spoken by ElevenLabs and cached by content, so the same
sentence is never paid for twice; before each synthesis the account is checked against
`monthly_cap` and `keep_in_reserve`, and intrf4ce falls back to the system voice rather than
overspend. Give the key as a path to a file (`elevenlabs.key_file`), in `secrets.yaml`, or in
`ELEVENLABS_API_KEY` — never in `settings.yaml`, which is meant to be shareable:

```yaml
elevenlabs:
  key_file: 'C:\path\to\.env'   # the file may hold the bare key or KEY=value
```

The voice in use is in `get_state`, and in the status line when a voice declines: "ElevenLabs
(0 of 30,000 characters used this month)" means the key was found.

## Building

.NET 10 SDK.

```bash
dotnet test                              # 81 tests
dotnet run --project src/Intrf4ce.App
./scripts/package.ps1 -Runtime win-x64   # one self-contained exe in dist/
```

`intrf4ce --selftest render <folder>` renders one of every element type offscreen and writes PNGs,
which is the quickest way to see whether the hidden renderer works on a machine, and
`./scripts/smoke-linux.ps1` runs the Linux build in a container to catch a missing native library.

**Platforms.** Windows is what this was built and tested on. The Linux build needs WebKitGTK for
the hidden renderer and espeak-ng or Piper for a voice; macOS uses `say` and is not yet verified.

## How it is put together

- `Intrf4ce.Core` — deck model, validation, layout, cues, settings. No UI, no platform.
- `Intrf4ce.Media` — images and EXIF, voices, audio.
- `Intrf4ce.Agent` — the MCP server, the tool catalog, and the hosted Claude Code process.
- `Intrf4ce.App` — Avalonia: the stage, the camera, the element views, the HUD.

Avalonia in code, no XAML and no MVVM; the window is the composition root and builds everything by
hand. More in [docs/architecture.md](docs/architecture.md) and the design in
[docs/plans](docs/plans/2026-09-22-intrf4ce-design.md).

## Licence

MIT, see [LICENSE](LICENSE). Vendored JavaScript libraries and their licences are listed in
[VENDORED.md](VENDORED.md); third-party notices in [NOTICE.md](NOTICE.md).

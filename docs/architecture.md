# How intrf4ce is put together

The design and the decisions behind it are in
[plans/2026-09-22-intrf4ce-design.md](plans/2026-09-22-intrf4ce-design.md). This is the shorter
map of the code as built.

## One executable, two roles

`intrf4ce` is the window. `intrf4ce --mcp` is a byte pump: it speaks JSON-RPC on stdin and stdout
and forwards it over a per-user named pipe (`intrf4ce.<user>`, `CurrentUserOnly`) to the window,
starting the window if none is running. The first line on the pipe is a hello saying which kind of
session this is — `hosted`, `attached`, `channel` or `activate` — which is also how a second launch
brings the existing window forward instead of opening another.

That way an agent registers one command, and the user never runs a server.

## Projects

| | |
|---|---|
| `Intrf4ce.Core` | deck model and JSON, validation, slide and callout and gallery layout, cue parsing and speech timing, settings, event hub. No Avalonia, no platform: everything here is testable without a window. |
| `Intrf4ce.Media` | image decoding and EXIF, asset fetching, TTS engines (ElevenLabs, SAPI, `say`/espeak/Piper), audio playback and loudness envelopes. |
| `Intrf4ce.Agent` | the MCP server and its tool catalog, the tool handlers, and the hosted Claude Code process. |
| `Intrf4ce.App` | Avalonia: the stage and camera, element views, the hidden renderer, the HUD. |
| `Intrf4ce.Plugins` | what a plugin is built against — `IPlugin`, `ElementType`, `LiveElement` — and the code that reads, checks and loads a plugin file. Avalonia and this are all a plugin shares with the app. |
| `plugins/*` | the built-in plugins, each its own project, packed by `plugins/Directory.Build.targets` into an `.i4p` the app carries as a resource. |

Avalonia is used in code only — no XAML, no MVVM, no DI container. `MainWindow` is the composition
root: it builds every object by hand, so the whole graph reads in one file and a missing dependency
fails to compile rather than at startup.

## The stage

Slides live on a **world canvas**, five to a row, each 1600×900 units. Moving between slides is a
camera flight (van Wijk and Nuij's smooth zoom-pan, the same interpolation d3 uses), not a
crossfade, so the deck keeps its geography: overview (`O`) pulls back to the whole thing and the
user watches it grow as the agent adds slides.

Focusing a part asks the element view where that part is, flies the camera to it, and drops a
feathered **spotlight** over everything else. The spotlight is drawn in screen space and follows
the camera matrix, so it stays sharp at any zoom.

Every element type is an `ElementView`: it loads asynchronously (downloads, decodes, renders),
reports its problems as issues rather than throwing, and answers three questions — what parts do
you have, where is a part, and what is at this point. That last one is how a click anywhere on the
stage becomes "focus the ACL callout" or "ask about the Saturn row" without each view handling
pointer events.

## Maps

A `map` element is drawn natively from OpenStreetMap tiles: the Web Mercator arithmetic is in
`Core` (a place must land on the pixel its own tile draws it at), the tiles are fetched a couple
at a time and cached on the machine, and markers and routes are drawn over them. Focusing a place
moves the map's own frame rather than magnifying the picture — zooming a map of Africa does not
show you the streets of Cairo — and until the new tiles arrive the ones already held are drawn
scaled, so there is always a map rather than a hole.

## The hidden renderer

Mermaid, TeX and Vega-Lite are rendered by the platform's own web engine in a `WebView` that is
never shown: the vendored libraries are injected as script, the SVG comes back with every computed
style inlined, and Avalonia draws it with Svg.Skia. So diagrams stay vector under the camera, and
text is flattened into absolute-baseline runs first because Svg.Skia does not lay out tspans the
way a browser does.

Part boxes (a Mermaid node's rectangle) are measured in the browser and returned with the SVG,
which is what makes `[[focus flow.B]]` possible.

## Sessions

A session is a folder under `sessions`, beside the executable: `deck.json` plus `assets/` holding
every image the presentation uses. Anything an agent puts on a slide is brought inside — a URL is
downloaded, a file from elsewhere on the machine is copied — and the deck records where each
source landed, so nothing is fetched twice and the folder still presents on a machine that has
never seen the original. A gallery whose folder has since gone falls back to the copies the
session kept.

Narration lives there too, under `audio/`: clips are named by content, so a presentation that
has been spoken once is spoken for ever, and a folder carried to another machine plays without a
key. A machine-wide cache sits behind the session and is copied from, never depended on.

The deck also keeps the conversation that built it and which slides have been performed.
Reopening one restores the slides, puts those questions back in the panel, puts the hosted agent
back on the conversation that built it (the session id is kept on the deck), and tells the agent
what it is looking at before the user asks for anything. A picture already on the machine is
referenced where it lives rather than copied, unless `library.copy_media` says the session has to
travel.

Deleting one (`dd`) moves its folder, and the hosted agent's transcript of it from Claude Code's
own folder, into `sessions/.trash`; `u` moves them back, and the trash is emptied on the way out
and, after a crash, on the way in. Vim's way: the key acts at once, and undo is the safety.

## Decks

A deck is a folder: `deck.json` plus the assets an agent downloaded. `DeckService` holds one
immutable snapshot, replaces it on every change, raises a `Changed` event, and autosaves 400 ms
later. Because the deck is immutable, the stage can read a snapshot on the UI thread while tool
calls arrive on others.

Slides and elements are records with a `type` discriminator; JSON is strict (an unknown field is
an error, not a shrug) and validation is path-precise, so an agent's mistake comes back as
`elements[1].callouts[0].at: expected [x, y] as fractions within 0..1` rather than a stack trace.

The discriminator is read by `ElementJsonConverter` rather than System.Text.Json's own
polymorphism, which refuses a type it was not told about at compile time. intrf4ce's own types
become their records; any other becomes a `PluginElement` holding its fields exactly as written,
so a deck made with a plugin opens without it — showing a card that names what is missing — and
is whole again when the plugin is back. Whether a type exists is the validator's question, asked
of what the agent sends, against `ElementTypes`: the plugins loaded now.

## 3D models

A `model3d` is drawn by `ModelCanvas` without a 3D API. Each frame the corners are turned and
projected on the CPU — a few thousand of them take well under a millisecond — the faces are sorted
far to near and shaded by their angle to one light, and the result goes to Skia, on the GPU, in as
few calls as possible: every face in one `DrawVertices` triangle mesh, the wires in a handful of
`DrawPoints` batches by colour and depth. Because it is ordinary Avalonia drawing, the stage's
camera, spotlight, fades and snapshots all work on it unchanged; a native OpenGL surface would
have needed each of those rebuilt, and would not have scaled with the camera.

The model itself is built in Core (`Shapes/`), testable without a window: primitives with their
faces wound outwards and their natural wires (a sphere's latitudes and longitudes, not its
triangles), `.obj` and `.stl` readers, a vertex-clustering thinner for scans over 40,000
triangles, and, for meshes that came as triangles, wires only along creases and borders. Labels
are laid out by the same `CalloutLayout` as an image's callouts, each kept to the side its part
is on so a turning model's labels do not swap gutters. Every change of the way it faces — the
agent's rotation, a focused part, J and K — is a turn, never a jump; a drag follows the pointer;
and a spinning model three slides away asks for no frames.

## Plugins

A plugin file is a zip of `plugin.json`, `prompt.md` and assemblies. It is read whole and checked
— its id, the contract version it was built for, that its entry assembly is there — before any of
its code runs. Its assemblies go into a load context of their own, which hands back the app's
copy of anything the app has, so a plugin's Avalonia controls are the stage's and its `IPlugin`
is the app's. Its one `IPlugin` class registers element types and tools; a name intrf4ce or
another plugin has is refused, and tool names must start with the plugin's id.

What a plugin adds goes three ways. The element types go to `ElementTypes` in Core, where the
validator checks fields against the plugin's schema and runs its check. The types, tools and
`prompt.md` go to the `ToolCatalog`, which extends the `element` definition before resolving refs
— each plugin field described with its type first, as the built-in fields are — and appends the
guides to the instructions; sessions hold the catalogue and are sent `tools/list_changed` when it
changes. And `PluginElementView` hosts the plugin's `LiveElement` on a slide: made once, told
each new version of its fields, asked for its parts when a cue focuses one, and disposed when
the view leaves the stage, which is how a plugin's clock stops.

Packages come from `plugins.load` paths, then the `plugins` folder, then the executable, first
wins per id. The built-in ones are packed at build time and embedded, then loaded through the
same path as any installed plugin, which keeps that path honest. Nothing is ever unloaded: .NET
cannot unload an assembly while objects made from it live, and a slide's controls are such
objects, so turning a plugin off takes effect on the next start.

## Narration

The narration text carries cues inline. The parser strips them, remembers where they were, and
`SpeechTiming` maps character positions to seconds — from ElevenLabs' character alignment when it
spoke, or estimated over the clip's real length and pinned to the pauses found in the audio when
a local voice did. Cues fire 250 ms early so the camera is already moving as the word is said.

Captions are split into sentences and the spoken word is highlighted. A slide that the agent has
not built yet is waited for rather than skipped.

## The window

Three columns: the sessions on disk, the stage, and the conversation. The chat used to float over
the bottom of the stage, which meant the agent's answer covered the slide it was about; now it has
a column of its own, holds every message rather than fading them, and collapses with `p` to a
strip holding the agent orb — still visible, still a way back in — while the stage takes the
window. The sessions column collapses the same way once something is on the stage, keeping the
settings within reach at its foot.

On the stage itself: the performance controls in one bottom corner (play this slide, keep going
through the deck, the voice, the subtitles) and the deck's own arrows in the other, because moving
through a deck is the reader's and playing is the presentation's. Double-clicking a picture gives
it the whole column, where a box dragged over any part of it can be asked about; the question
carries that box in the picture's own coordinates, which are the ones callouts are written in.

Settings are a panel over the stage. Each control writes one line of `settings.yaml` and lets the
watcher apply it, so there is one way settings change, whoever changes them — the panel, a text
editor, or the agent's `set_setting`. The list of what can be changed lives in `SettingsSchema`,
which is what the panel draws and what the agent's tool will accept.

## Keys and commands

Everything the app does is a command: a method carrying `[Command]`, found by reflection, so
`:help`, completion and the key map all read the one list and cannot disagree with it. A key
runs a command line — `next`, `go 3`, `set subtitles!` — through a `KeyMap` of shipped
defaults with the user's `keys:` from settings.yaml layered over them, and a `KeyReader` that
turns presses into sequences (`gg`), counts (`3l`) and a pause that forgets a stray key. The
notation is Fjord's; the code is intrf4ce's own, since Fjord's key layer is a port of
qutebrowser's and that is GPL-3.0.

## Events back to the agent

Chat messages, quiz answers and "ask about this" clicks go to exactly one place: the hosted
agent's stdin, or an attached session that opened a Claude Code channel, or the queue that
`wait_for_user` drains. Sending them everywhere would have two agents answer the same question.

Every one of them carries where the user was: the slide, what was focused on it, what was
playing, and which slides they have not yet heard. Without that, "how deep is that?" reaches the
agent as four words about nothing. The agent can work the same controls back through
`set_playback` — including playing a range of slides, which is how the answer to a new question
gets performed without replaying the deck.

Files dropped on the window travel the same way, as one `<intrf4ce-attachment kind=… path=…/>`
line each after the words they were sent with. A path rather than the bytes: the agent already
has the tools to read a file — `inspect_image` for a picture, its own Read for anything else —
and a path is what an image element or a gallery wants. The conversation keeps the paths with
the line they went with, so a reopened presentation shows the pictures again, and the agent's
summary of a session lists them.

## Signing in

The hosted agent signs in however Claude Code is signed in, and that login expires. Claude Code
reports a request it could not make as an assistant message no model wrote, marked
`is_api_error_message`, with `error` saying why; `ClaudeCodeHost` turns those into errors rather
than lines the agent said, and `authentication_failed` into `SignInNeeded`. The turn's messages
are kept to ask again, and the process is stopped, so the next message starts one that reads
whatever login there is by then. `claude auth status --json` at start-up finds the same thing
before anyone has asked anything.

`ClaudeSignIn` runs `claude auth login` the way the agent is run — same command, environment and
folder — so it signs in the login the agent will use. Claude Code opens the browser and waits on
a port of its own for it to come back; intrf4ce only passes on the page it prints, for when no
browser came up, and the `code#state` that page shows, written to its stdin. Once it exits
cleanly the agent is restarted on its session and sent what went unanswered. intrf4ce never
handles a password or a token.

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

Reopening one restores the deck, puts the hosted agent back on the conversation that built it
(the session id is kept on the deck), and sends the agent a `resume` event so it knows what it
is looking at before the user asks for anything.

## Decks

A deck is a folder: `deck.json` plus the assets an agent downloaded. `DeckService` holds one
immutable snapshot, replaces it on every change, raises a `Changed` event, and autosaves 400 ms
later. Because the deck is immutable, the stage can read a snapshot on the UI thread while tool
calls arrive on others.

Slides and elements are records with a `type` discriminator; JSON is strict (an unknown field is
an error, not a shrug) and validation is path-precise, so an agent's mistake comes back as
`elements[1].callouts[0].at: expected [x, y] as fractions within 0..1` rather than a stack trace.

## Narration

The narration text carries cues inline. The parser strips them, remembers where they were, and
`SpeechTiming` maps character positions to seconds — from ElevenLabs' character alignment when it
spoke, or estimated over the clip's real length and pinned to the pauses found in the audio when
a local voice did. Cues fire 250 ms early so the camera is already moving as the word is said.

Captions are split into sentences and the spoken word is highlighted. A slide that the agent has
not built yet is waited for rather than skipped.

## The window

Two columns: the stage, and the conversation beside it. The chat used to float over the bottom
of the stage, which meant the agent's answer covered the slide it was about; now it has a column
of its own, holds every message rather than fading them, and collapses with `T` to a strip
holding the agent orb — still visible, still a way back in — while the stage takes the window.

## Events back to the agent

Chat messages, quiz answers and "ask about this" clicks go to exactly one place: the hosted
agent's stdin, or an attached session that opened a Claude Code channel, or the queue that
`wait_for_user` drains. Sending them everywhere would have two agents answer the same question.

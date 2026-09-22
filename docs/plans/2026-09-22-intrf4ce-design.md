# intrf4ce — design

A live presentation surface that an agent drives over MCP. The user asks for something ("explain
the knee", "show me the Italy photos", "how is a phone put together"), the agent builds slides in
the background, and the app performs them: images with labelled callouts, diagrams that focus
step by step, narration that is spoken and subtitled, all while the user watches, zooms, hovers,
answers quizzes and keeps talking through a chat box.

North star: Apple's *Knowledge Navigator* (1987) and the zoomable interfaces of the 90s (Pad++),
built so that they are actually usable: calm, legible, and fast.

Decisions made on 2026-09-22:

| Question | Answer |
|---|---|
| Rendering | **Native Avalonia stage + hidden web renderer.** Avalonia draws everything; a hidden WebView turns Mermaid, LaTeX and Vega-Lite into SVG that is drawn natively. |
| Agent connection | **Both, hosted by default.** The app runs Claude Code in the background; any other MCP agent can attach through `intrf4ce --mcp`. |
| Platforms | **Cross-platform by design, Windows first.** The seams exist from day one; the first prototype is built and verified on Windows, and the macOS/Linux engines land in M4. |
| Voice | **ElevenLabs in M1**, not M4. The system voice stays as the free fallback. |

## Three principles

1. **The agent says *what*, the app decides *how*.** The agent supplies content, anchors in
   normalised coordinates, order and cues. Fonts, palette, motion curves, subtitle style and
   voice are user settings. The agent never sets a colour or a duration.
2. **Latency asymmetry.** A tool call takes seconds; a frame takes 16 ms. So choreography is
   declarative: narration carries inline cues (`[[focus knee.acl]]`) that the app fires in sync
   with speech. Live control (`focus`, `go_to`, `say`) exists, but the script is the default.
3. **The agent can see.** `snapshot` returns the stage as a PNG and `inspect_image` returns an
   image with a coordinate grid, so callouts land on the anatomy rather than near it.

## Architecture

```
intrf4ce (GUI, single instance) ─────────────────────────────────────────────────────────┐
│  Stage (Avalonia, world space)   slides on a 2D canvas · camera flights · element views │
│  HUD (screen space)              subtitles · conversation panel (orb, chat) · toasts   │
│  Render service                  hidden NativeWebView ─ mermaid · MathJax · Vega-Lite  │
│                                    → self-contained SVG + part boxes → Svg.Skia        │
│  Narration                       TTS engine → audio cache → SoundFlow → cue clock      │
│  Deck store                      Documents/intrf4ce/<deck>/deck.json + assets + audio  │
│  MCP server                      JSON-RPC 2.0 sessions over a per-user named pipe      │
│  Hosted agent bridge             claude -p --input-format stream-json … (child)        │
│  Event hub                       chat · quiz · ask-about · nav → agent                 │
└──────────────────────────────────────────────▲──────────────────────────────────────────┘
                                               │ named pipe "intrf4ce-<user>" (CurrentUserOnly)
             intrf4ce --mcp  (stdio shim) ─────┘  spawned by any agent; launches the GUI if absent
```

**One executable, two roles.** `intrf4ce` is the GUI. `intrf4ce --mcp` is a byte pump between
stdin/stdout and the GUI's named pipe; it knows nothing about MCP, so session state lives in one
place. It starts the GUI if none is running and waits up to ten seconds for the pipe. Both the
hosted agent and attached agents use this same entry point:

```
claude mcp add intrf4ce -- C:\Tools\intrf4ce.exe --mcp
```

The MCP protocol is hand-rolled (initialize, ping, tools/list, tools/call, and outbound
notifications). It is small, and the tool schemas *are* the product: they live as reviewed JSON
documents, which reflection-generated schemas from the official SDK would not give us. This
follows equaliser's server.

### Projects

```
src/Intrf4ce.Core    deck model, JSON validation, cue parser, narration timeline,
                     callout and gallery layout, slide auto-layout, event contracts   (no Avalonia)
src/Intrf4ce.Agent   JSON-RPC/MCP sessions, tool registry + schemas, pipe transport,
                     stdio shim, hosted Claude Code bridge                            (Core)
src/Intrf4ce.Media   TTS engines, audio cache and playback, EXIF scan, image fetch    (Core)
src/Intrf4ce.App     Avalonia UI, render service, settings, composition root          (all)
tests/Intrf4ce.Tests xUnit + FsCheck
```

House rules carried over from Fjord: net10.0, Avalonia 12, central package management,
`TreatWarningsAsErrors`, **no XAML, no MVVM, no DI container** (`MainWindow` wires by hand),
trimming off, single-file self-contained publish per RID. Deliberate divergence: no vendored
modal keyboard layer. A presentation surface needs about a dozen keys, not a command language.

### Cross-platform seams

| Concern | Windows | macOS | Linux |
|---|---|---|---|
| Hidden web engine (`Avalonia.Controls.WebView`) | WebView2 (preinstalled) | WKWebView | WPE or WebKitGTK (`libwebkit2gtk-4.1` required) |
| System voice (`ITtsEngine`) | SAPI (`System.Speech`) | `say` | `espeak-ng` / `spd-say` |
| Neural voice, any OS | ElevenLabs (with timestamps), Piper (optional local) | | |
| Audio | SoundFlow (miniaudio natives for all three) | | |
| Pipe | named pipe | Unix socket (same .NET API) | Unix socket |

The first prototype (M0 to M3) is Windows only: WebView2, SAPI, ElevenLabs. The `say`, espeak
and Piper engines and the non-Windows checks come in M4. Linux then gets a container smoke test
under Xvfb where feasible; macOS builds but cannot be run here, and the README must say so.

## Document model

A **deck** is a folder: `deck.json` and `assets/`. Decks autosave on every change, because the
agent builds incrementally and a crash must not lose its work.

As built, `assets/` holds what was downloaded from a URL; a file already on the machine is
referenced where it lies rather than copied, so a gallery of a holiday folder does not duplicate
gigabytes of photographs. The element keeps the source exactly as the agent wrote it, and the
deck records where each download landed, so reopening finds the file instead of fetching it
again. Narration clips are cached per machine under `cache/voice`, shared across decks, since
the same sentence should never be paid for twice.

```json
{
  "id": "ligaments",
  "title": "Four ligaments hold the knee together",
  "elements": [
    { "type": "image", "id": "knee", "src": "https://upload.wikimedia.org/…/knee.png",
      "callouts": [
        { "id": "acl", "at": [0.47, 0.44], "label": "ACL",
          "detail": "Anterior cruciate ligament: stops the tibia sliding forward." },
        { "id": "pcl", "at": [0.53, 0.52], "label": "PCL" },
        { "id": "mcl", "at": [0.31, 0.55], "label": "MCL" },
        { "id": "lcl", "at": [0.70, 0.56], "label": "LCL" } ] },
    { "type": "markdown", "id": "notes",
      "text": "- **Cruciates** cross inside the joint\n- **Collaterals** brace the sides" }
  ],
  "narration": "Four ligaments hold the knee together. [[focus knee.acl]] Inside the joint, the ACL stops the shin bone sliding forward, [[focus knee.pcl]] and the PCL stops it sliding back. [[focus knee.mcl]] On the sides, the collaterals [[focus knee.lcl]] stop it buckling. [[clear]]",
  "advance": "auto"
}
```

### Elements

Common fields: `id`, `type`, optional `area`, `rect` (free layout only), `caption`, `hidden`
(revealed by a `show` cue).

| type | agent supplies | addressable parts |
|---|---|---|
| `title` | `text`, `subtitle`, `kicker` | — |
| `markdown` | `text` (CommonMark + tables + `$…$` / `$$…$$` math) | heading slugs |
| `image` | `src` (path or URL), `fit`, `callouts[]` with `at:[x,y]` or `box:[x,y,w,h]`, `label`, `detail`, `side` | callout ids |
| `diagram` | `mermaid`, optional `steps:[nodeId…]` | Mermaid node ids |
| `math` | `tex` | — |
| `chart` | `kind` (bar, line, area, pie, donut, scatter) + `data` + `x`/`y`/`series`, **or** a full `vegaLite` spec | — |
| `table` | `columns`, `rows`, optional `key` column | row keys |
| `gallery` | `items[]` (`src`, `caption`) and/or `folder`; `arrange`: scatter, grid, timeline | item ids |
| `quiz` | `question`, `options`, `multiple`, optional `answer`, `explanation` | — |

Coordinates are fractions of the image (0..1, x right, y down), never pixels, so they survive
any window size.

**Layout.** Slides are 1600×900 logical units. `layout` is `full`, `split`, `wide` (main +
side), `stack`, `grid` or `free`; when omitted it is chosen from the element count and kinds
(one → full; a visual plus text → wide; four → grid). The agent picks order; the app picks
geometry.

### Narration and cues

Cues inside narration: `[[focus el.part]]` (shorthand `[[el.part]]`), `[[zoom el x y w h]]`,
`[[show el]]`, `[[hide el]]`, `[[clear]]`. The parser strips them and records each one's
character offset in the clean text. The engine then maps offsets to time:

- ElevenLabs `with-timestamps`: character-accurate.
- SAPI: word boundaries from `SpeakProgress`.
- `say`, espeak, Piper, or subtitles-only: proportional estimate over the measured clip length,
  weighted for punctuation pauses. That is good enough for sentence-level sync.

Subtitles show the current sentence (two lines at most) with the spoken word highlighted when
timing allows. Narration is synthesised as soon as a slide exists, so audio is ready before the
user arrives. When it finishes, an `auto` slide advances after a pause (a setting); a `manual`
one waits. Arriving at a slide the agent has not built yet shows "the agent is still building
this" and, in auto mode, continues when it lands.

**Voice budget.** ElevenLabs (Starter: 90,000 chars/month, shared with other projects) is the
preferred voice once a key is configured (`secrets.yaml` or `ELEVENLABS_API_KEY`).
Clips are content-addressed exactly as in `dev/11labs`: sha256(text + voiceId + modelId +
settings) truncated to 32 hex, so nothing is ever billed twice. Before each synthesis the app
checks `/v1/user/subscription` against a user cap and falls back to the system voice rather than
overspend. Without a key the system voice is used.

## The stage

- **Slides live on a world canvas** (a row-major grid). Moving between slides is a camera
  flight, not a crossfade. **Overview** (`O`) zooms out to the whole deck, so the user watches
  the presentation grow as the agent adds slides.
- **Focus.** Every element owns a viewport camera. Focusing a part zooms that element's content
  to the part's box and drops a feathered spotlight over everything else. Callouts pulse; diagram
  nodes glow; table rows lift.
- **Callouts** use boundary labelling, the anatomy-atlas look: labels stacked in gutters beside
  the image, sorted by anchor height so leader lines never cross, with elbows. Hover shows
  `detail`.
- **Gallery.** *Scatter* is a seeded, relaxed pile of prints with small rotations. *Grid* is
  justified rows. *Timeline* orders by EXIF date. Hover lifts a print and shows its metadata;
  click flies the camera to it. EXIF orientation is honoured, since phone photos are usually
  rotated.
- **Visual language**: dark ink stage, one warm accent for focus, one cool accent for the agent,
  1 px leaders with anchor dots, staggered 300 ms entrances. The agent orb (in the panel) breathes
  when idle, orbits while the agent works, and moves with the voice while speaking. The status
  line under the orb says what the agent is doing ("Searching the web", "Building slide 4").

**Keys.** `→`/`PgDn` next · `←`/`PgUp` previous · `Space` play/pause narration · `O` overview ·
`Esc` unfocus/back · `Enter` or `/` chat · `M` mute · `S` subtitles · `F` fullscreen ·
`L` library · `?` help. Mouse: wheel zooms inside an element, drag pans, click a callout or node
to focus it, right-click any part → **Ask about this** (prefills the chat with a reference).

## Agent interface

### Tools

| tool | purpose |
|---|---|
| `get_state` | deck summary (slides, readiness, errors), current slide, playback, stage size, voice budget |
| `list_decks`, `new_deck`, `open_deck` | deck lifecycle |
| `put_slide` | create or replace a whole slide (upsert by id; `after` positions it) |
| `update_slide` | incremental: set fields, upsert or remove elements, for live building |
| `remove_slide`, `move_slide` | structure |
| `go_to`, `focus`, `say` | live control; `say` speaks without saving |
| `reply` | chat message to the user |
| `wait_for_user` | long-poll for events (attached agents without channels) |
| `snapshot` | PNG of the stage, or of any slide rendered offscreen |
| `inspect_image` | an image with a labelled 0..1 grid, its size and EXIF, so anchors are measured |
| `scan_media` | images in a folder with date, GPS, camera and dimensions |

`put_slide` waits (up to a few seconds) for rendering, so a Mermaid syntax error, a missing
file or a 404 comes back in the result for the agent to fix, and is also shown on the stage as an
error card. Server `instructions` carry the authoring guide: build slide 1 fast, one idea per
slide, cue parts as they are mentioned, measure before placing callouts.

### Events to the agent

`chat {text}`, `ask {target, text}`, `quiz {slide, element, selected, correct}`,
`nav {slide, index, atEnd}` (only when the user reaches the newest slide).

| session kind | delivery |
|---|---|
| hosted | written to the agent's stdin as user messages (`stream-json`) |
| attached with `--mcp --channel` | `notifications/claude/channel` (Claude Code channels, research preview) |
| attached, plain | queued for `wait_for_user` |

The shim flag makes channel delivery explicit, because Claude Code never acknowledges channel
notifications and duplicating events across two paths would confuse the agent.

### Hosted agent

`claude -p --input-format stream-json --output-format stream-json --verbose
--mcp-config {intrf4ce: <self> --mcp} --append-system-prompt <presenter guide>
--permission-mode dontAsk --allowedTools mcp__intrf4ce,WebSearch,WebFetch,Read,Glob,Grep`,
working directory `Documents/intrf4ce/agent`. It starts with the app and stays warm. Assistant
text becomes chat bubbles; `tool_use` events drive the status line; `result` updates session cost.
The deck records the session id, so reopening a deck resumes its conversation. Command, model,
tools and permission mode are settings. If the process crashes it is restarted with `--resume`.
Not `--bare`, which would require an API key instead of the subscription login.

## Settings

YAML in the platform config directory, hot-reloaded, generated with comments on first run:
theme (dark/light, accent, font scale), stage (subtitle placement, animation speed), narration
(engine, voice, rate, auto-advance, pause, reading speed), ElevenLabs (voice, model, monthly cap),
agent (mode, command, model, tools, permission mode, working directory), library folder. Secrets
(ElevenLabs key) live in `secrets.yaml`, git-ignored by construction, never logged, never sent to
the agent.

## Failure behaviour

| failure | behaviour |
|---|---|
| invalid spec | tool error naming the JSON path (`elements[2].callouts[0].at: expected [x, y] within 0..1`) |
| render error | returned from `put_slide`, error card on stage with the source |
| image missing or 404 | warning in result, placeholder on stage |
| TTS failure or budget cap | next engine, then subtitles only; one notice to user and agent |
| no web engine | diagrams, math and charts show their source with a notice; everything else works |
| hosted agent exits | orb turns red, chat says so, restart with `--resume` |
| GUI exits under an attached agent | shim exits non-zero; the agent reports the server as failed |

Security: the pipe is current-user only; downloads are size-capped and type-checked; the render
WebView loads only vendored local scripts and blocks network requests; Mermaid runs with
`securityLevel: strict`; links open only on a user click.

## Testing

- Core: JSON round-trip and validation messages; cue parser (FsCheck: stripping cues preserves
  the text, offsets monotonic); callout layout properties (no label overlap, no leader crossing,
  inside bounds); timeline mapping; auto-layout.
- Agent: JSON-RPC handshake and tool calls over an in-memory duplex stream.
- **The test that earns its keep:** every tool in the schema file has a handler and every handler
  has a schema; every element type in the schema has a renderer, built by the same method the app
  uses (the lesson from Fjord's binding test).
- Render service integration test, skipped when no web engine is available.
- End to end: drive the real app from Claude Code over MCP and keep the screenshots in
  `docs/media`.

## Spikes first

1. Hidden `NativeWebView` renders Mermaid, MathJax and Vega to SVG (Windows here; Linux in a
   container).
2. Svg.Skia 5.1.1 (the newest line on SkiaSharp 3.119, which Avalonia 12 uses) draws that SVG
   faithfully once styles are inlined. Fallback: rasterise in the browser at high DPI.
3. Claude Code `stream-json` round trip with the shim as its MCP server.
4. SoundFlow playback with position reporting.

## Milestones

| | scope |
|---|---|
| **M0 Foundation** | repo and house props; MCP core, pipe, shim; deck model and store; stage world, camera, navigation; `title`, `markdown`, `image`; deck, slide, `go_to` and `snapshot` tools; spikes |
| **M1 Narrated anatomy** | callouts, spotlight focus; narration with **ElevenLabs** (timestamps, content-addressed cache, budget guard) and SAPI fallback; subtitles, cues, auto/manual advance; chat bar; hosted Claude Code; `wait_for_user`, `reply`; `inspect_image`; orb and status |
| **M2 Rich renderers** | render service; `diagram` with focusable nodes and steps; `math` and Markdown math; `chart` |
| **M3 Photos, data, interaction** | `gallery`, `scan_media`, `table`, `quiz`, ask-about-this |

All five milestones are built, with these caveats: the Linux and macOS builds compile and
publish but have only been exercised on Windows, the container smoke test needs a runtime that
was not running when it was written, and the `say`, espeak and Piper voices are written from
their documented command lines rather than tried.

What M3 added beyond the plan: clicking any part of a slide focuses it and
clicking it again goes back, a transcript panel (`T`), and part names that come from content —
table rows and a folder's photos — which are slugs ("Knee joint" is `knee-joint`, `IMG_2031.jpg`
is `img-2031`) so the validator can check a cue before anything is drawn.
| **M4 Cross-platform and polish** | `say`, espeak and Piper engines; Linux container smoke test; channels; overview; library; settings hot reload; four-RID packaging; README |

Not in v1 (YAGNI until asked): video, embedded live web pages, a native "exploded layers"
element, map view for GPS photos, collaborative decks, export to PPTX/PDF.

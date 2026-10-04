# Plugins, live elements, and deleting a session

October 2026. Three requests: a plugin system that brings display elements, behaviour and the
prompts that go with them together in one file; elements whose data keeps itself current
alongside the agent; and `dd` to delete a session completely.

## What a plugin is

One file, `name.i4p`: a zip holding

```
plugin.json     id, name, version, description, api, entry assembly, user prompts
prompt.md       how the agent should use what the plugin adds
*.dll           the code: the entry assembly and anything it brings that the app does not
```

The code is a .NET assembly built against `Intrf4ce.Plugins`, the only part of intrf4ce a
plugin sees. Its entry class implements `IPlugin` and, given a registrar, adds:

- **element types** — a name the agent writes in `"type"`, a description and JSON schema for
  its fields, a check for those fields, and a factory for a `LiveElement`: an Avalonia control
  plus what it shows in words, its parts for focus cues, and its own clock;
- **agent tools** — named with the plugin's id first, so two plugins cannot collide;
- nothing else yet. User prompts come from the manifest, not code.

### Why assemblies, not scripts or web pages

The components the request names already exist, in Kompas, as Avalonia controls drawn by
hand. As assemblies they move across almost unchanged and draw natively: sharp at any zoom,
in the stage's colours, inside the camera's transform. A web view was the other candidate
and fails on the last point — a native browser window does not scale or pan with the
stage. A script engine with a declarative view language would make plugins easier to write
(an agent could write one) but means a second UI toolkit to design and a rewrite of every
component; it can come later as a plugin type of its own if wanted.

The cost is that a plugin is code with the app's permissions. That is said where plugins are
installed, and the built-in three are ours.

### Loading

Each package is read whole, its manifest checked (`api` must be one this build speaks), and
its assemblies loaded into a load context of their own that defers to the app for anything
the app already has — Avalonia, `Intrf4ce.Plugins`, the runtime — so a plugin's controls are
the app's controls. Packages come from three places, first wins per id:

1. `plugins.load` in settings.yaml — paths, for someone working on a plugin who wants each
   build picked up without installing it;
2. `plugins/` beside `settings.yaml` — what the user installed (`:plugin install file.i4p`);
3. the executable itself — timezones, weather, holidays, built and packed with the app.

`plugins.disabled` in settings.yaml lists ids not to load. Assemblies cannot be unloaded
from a running .NET process without unloading everything they touched, so disabling takes
effect on restart; installing takes effect at once.

## The deck model

An element whose type no built-in knows becomes a `PluginElement`: the common fields typed
(id, area, rect, caption, hidden), its own fields kept exactly as written. It round-trips
through deck.json whether or not its plugin is loaded, so a deck made with a plugin opens
without it, showing a card that says which plugin it needs, and comes back whole when the
plugin is installed. The type is checked against the plugins that are loaded when the
agent sends it; on disk, anything goes.

## The agent

The tool catalogue gains each plugin's element type in the `element` schema (its name in
the enum, its fields described with its name first, as the built-in fields are), each
plugin tool, and each plugin's `prompt.md` appended to the instructions. Installing a
plugin at run time updates the catalogue in place and tells connected agents the tool list
changed. One new core tool, `read_element`, returns what a live element shows right now in
words — the time in Zagreb, today's high — so narration can say what the slide says.

## Live elements

A live element owns its refresh: a clock ticks each minute on the minute, the weather
refetches every fifteen minutes, holidays every twelve hours, all through one shared
`HttpClient` and with in-memory caches so three slides of the same city ask once. The agent
changes one the way it changes anything (`update_slide`), and the element takes the new
fields without being rebuilt. When its slide goes, its clock stops.

## The three built-ins

From Kompas (MIT, same author), generalised where Kompas was personal:

- **timezones** — the 24-hour timeline, one band per place shaded by what the hour means
  there, a now line, daylight-saving dates. Zones are IANA ids; the first is home.
- **weather** — now (condition, temperature, feels-like, wind), a day strip, and the 72-hour
  temperature and rain trend with the UV peak and sunrise and sunset, from Open-Meteo, which
  needs no key. A place name is looked up with Open-Meteo's geocoder.
- **holidays** — upcoming public holidays for any country Nager.Date covers, with the
  regions each applies in and the home region's in full colour. Kompas was Australia only.

## Deleting a session

`dd` (`:delete`) deletes the open session, `:delete id` another, and right-click in the list
offers it too. Vim's `dd` acts at once and `u` undoes it, so this does the same: the session's
folder, and the hosted agent's transcript of it, are moved to a trash folder, `u` (`:undo`)
puts back the most recent, and the trash is emptied when the app closes. Completely, then —
but not irrecoverably by one stray keystroke.

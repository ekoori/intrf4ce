# Contributing

Issues and pull requests are welcome. A bug report is most useful with the log from the day it
happened (`%LOCALAPPDATA%\intrf4ce\logs` on Windows) and, for anything on the stage, the deck: the
session's `deck.json`.

## Building and testing

- .NET 10 SDK. `dotnet test` runs the suite; `dotnet run --project src/Intrf4ce.App` starts the app.
- `INTRF4CE_HOME=<folder>` gives a run its own settings, sessions, cache and pipe, so a test window
  never talks to the one you use every day. With `agent.mode: attached` in that home's
  `settings.yaml`, the stage can be driven over MCP without spending an agent's turns.
- `intrf4ce --selftest render <folder>` renders one of every element type offscreen, which is the
  quickest way to see that the hidden renderer works on a machine.
- `./scripts/package.ps1 -Runtime win-x64` builds the single-file executable into `dist/`.
- After scripted edits, build with `--no-incremental` before trusting what the app does: an
  incremental build can report success without compiling.

## How the code is written

- Avalonia in code: no XAML and no MVVM. The window is the composition root and builds everything
  by hand — see [docs/architecture.md](docs/architecture.md).
- Everything the app does is a command carrying `[Command]`, and every key runs one; a new action
  is a command first, then a key or a button.
- The agent's tools are a schema (`src/Intrf4ce.Agent/Resources/tools.json`) and handlers, and a
  test checks each against the other: a new tool needs both.
- Warnings are errors, line endings are LF (`.gitattributes`), and `.editorconfig` is the style.
- Comments say why, in plain sentences; a name says what.

## Licence

By contributing you agree that your contribution is licensed under the [MIT licence](LICENSE).

# Lookup — Flow Launcher Plugin

A local C# Flow Launcher plugin (action keyword `lu`) that fuzzy-searches local JSON
lookup datasets, starting with NAICS business classification codes and generic enough
to add other datasets later.

**Status:** pre-implementation. The directory currently holds only this file, `SPEC.md`,
and `logs/`. No source code exists yet.

**Full requirements:** see [`SPEC.md`](./SPEC.md). Build from that spec.

## Hard rule

Do not fabricate Flow Launcher API details. If the exact C# plugin API, `plugin.json`
schema, or project template is not already present here, verify against the official
Flow Launcher plugin docs or an existing working C# plugin before writing code.
The sibling `../Flow.Launcher/` checkout is a usable reference.

## Conventions

- JSON: `System.Text.Json` unless there is a strong reason otherwise.
- Search: prefer a dependency-free scorer; avoid heavy packages.
- Keep the plugin fast, local, portable; do not overengineer configuration.
- See `SPEC.md` → "Open technical decisions" for the items to pin first
  (target framework, full `plugin.json` fields, clipboard API, `IPlugin` vs `IAsyncPlugin`).

## Commands

Fill in once the project exists:

- Build: `dotnet build` — target framework TBD (see SPEC open decisions)
- Test: `dotnet test`
- Install: copy build output into the Flow Launcher plugins folder, then restart
  Flow Launcher (verify the exact path against the docs).
ye
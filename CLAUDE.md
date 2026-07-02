# Lookup — Flow Launcher Plugin

A local C# Flow Launcher plugin (action keywords `lu` = all datasets, `na` = NAICS
only) that fuzzy-searches local JSON lookup datasets, starting with NAICS business
classification codes and generic enough to add other datasets later.

**Status:** implemented. `Lookup/` holds the plugin (net9.0-windows, `IPlugin` +
`IContextMenu` + `IReloadable`), `Lookup.Tests/` the xUnit suite for the SDK-free core
(models + services compiled directly into the test assembly — new `Services/*.cs` files
must be added to `Lookup.Tests.csproj`'s `<Compile Include>` list).

**Full requirements:** see [`SPEC.md`](./SPEC.md).

## Hard rule

Do not fabricate Flow Launcher API details. If the exact C# plugin API, `plugin.json`
schema, or project template is not already present here, verify against the official
Flow Launcher plugin docs or an existing working C# plugin before writing code.
The sibling `../Flow.Launcher/` checkout is a usable reference.

## Conventions

- JSON: `System.Text.Json`; the on-disk dialect (snake_case, comments, trailing
  commas) is defined once in `Lookup/Services/JsonDefaults.cs`.
- Search: dependency-free tiered scorer (`Lookup/Services/Scorer.cs`); avoid heavy packages.
- Keep the plugin fast, local, portable; do not overengineer configuration.
- Indexed record fields are normalized with `TextUtils.Normalize` (the same treatment
  queries get); only `SearchRecord.TitleLower` stays un-collapsed for highlight indices.

## Commands

- Build: `dotnet build Lookup\Lookup.csproj -c Release` (flat output in `Lookup\bin\Release\`)
- Test: `dotnet test Lookup.Tests\Lookup.Tests.csproj`
- Install: `./build.ps1` — builds and copies into
  `%APPDATA%\FlowLauncher\Plugins\Lookup\`, preserving the installed `config.json`
  and `data\` files; restart Flow Launcher afterwards.

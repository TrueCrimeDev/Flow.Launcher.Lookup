# Lookup — Flow Launcher Plugin Spec

You are building a local Flow Launcher plugin in C# named Lookup.

## Goal

Create a Flow Launcher C# plugin that searches large local JSON lookup files with the action keyword `lu`. The first dataset is NAICS business classification codes, but the plugin must be generic enough to support other lookup datasets later.

## Core behavior

- Plugin name: Lookup
- Language: C#
- Platform: Flow Launcher plugin
- Action keyword: `lu`
- User input format: `lu <query>`
- Query can be a number, partial code, word, phrase, acronym, typo, or mixed text.
- Results must appear in Flow Launcher's dropdown as the user types.
- Results must be fuzzy matched and ranked by relevance.
- The plugin must work locally without network access.

## Primary dataset

Start with NAICS codes.

Support searches like:

- `lu 541511`
- `lu software`
- `lu computer systems`
- `lu farming`
- `lu construction`
- `lu payroll`
- `lu data processing`

For NAICS results, show:

- Title: `<code> - <title>`
- Subtitle: matched description, keywords, aliases, sector, or parent category
- Optional context: sector, parent code, notes, or URL if available

## Data design

Create a clean custom JSON schema that supports NAICS and future datasets.

Use this schema:

```json
{
  "dataset": "naics",
  "version": "2022",
  "items": [
    {
      "id": "541511",
      "code": "541511",
      "title": "Custom Computer Programming Services",
      "description": "Establishments primarily engaged in writing, modifying, testing, and supporting software.",
      "category": "Professional, Scientific, and Technical Services",
      "parent_codes": ["54", "5415"],
      "keywords": ["software", "programming", "developer", "computer systems", "applications", "code"],
      "aliases": ["software development", "custom software", "app development"],
      "url": ""
    }
  ]
}
```

Schema notes:

- `id` must be unique across **all** loaded datasets. Prefer a namespaced form (e.g. `naics:541511`) so codes do not collide when multiple datasets are loaded at once.
- `code` is the dataset-local identifier shown to the user and copied on Enter.

## Plugin requirements

- Use C# and the standard Flow Launcher C# plugin API.
- Include `plugin.json` with correct metadata and the `lu` action keyword.
- Load JSON files from a local `data` directory inside the plugin folder.
- Support multiple JSON files and multiple datasets.
- Validate JSON structure on load.
- Handle missing optional fields safely.
- Build an in-memory searchable index at startup.
- Do not re-read JSON files on every keystroke.
- Add `lu reload` to reload local JSON files without restarting Flow Launcher.
- Add `lu help` to show usage examples.
- Add `lu datasets` to show loaded datasets and item counts.
- Handle bad JSON with a visible Flow Launcher error result instead of crashing.
- Handle empty queries by showing help or examples.
- Handle no matches with a clear "No close matches found" result.

## Fuzzy search requirements

- Search across `code`, `title`, `description`, `keywords`, `aliases`, `category`, and `parent_codes`.
- Exact code matches rank highest.
- Prefix code matches rank very high.
- Exact phrase matches rank above loose fuzzy matches.
- Whole-word matches rank above weak typo matches.
- Typo-tolerant matches should still appear when close enough.
- Limit visible results to 10 to 20.
- Define a single numeric score scale so matches from different fields are directly comparable. Document the score band for each tier (exact code, prefix code, exact phrase, whole word, typo) in code comments.
- Decide whether `parent_codes` contribute to the score or only to display/context — matching them by score makes `lu 54` return every child of sector 54, which is usually noise.
- Keep ranking logic readable and explain non-obvious scoring choices in code comments.

## Implementation preference

- Prefer a lightweight dependency-free scorer unless a C# fuzzy matching package is clearly easy to package with a Flow Launcher plugin.
- Use `System.Text.Json` unless there is a strong reason to use another JSON library.
- Keep the plugin fast, local, portable, and easy to install.
- Avoid overengineering configuration.

## Project structure

Create this structure unless the existing Flow Launcher C# template requires a different layout:

```text
Lookup/
  plugin.json
  README.md
  Images/
    icon.png
  data/
    naics.sample.json
  Lookup.csproj
  Main.cs
  Models/
    LookupDataset.cs
    LookupItem.cs
    SearchRecord.cs
  Services/
    DataLoader.cs
    SearchIndex.cs
    Scorer.cs
    PluginConfig.cs
Lookup.Tests/                 # recommended — makes the verification list executable
  Lookup.Tests.csproj
  ScorerTests.cs
```

## Expected C# behavior

- `Main.cs` implements the Flow Launcher plugin entry point.
- Initialize the data loader and search index when the plugin starts.
- Query the in-memory index on each user search.
- Honor the `CancellationToken` Flow Launcher passes to `Query` so stale keystrokes stop work early and typing stays responsive.
- Return Flow Launcher result objects with title, subtitle, icon (`IcoPath`, e.g. `Images/icon.png`), score, and action.
- Pressing Enter copies the primary code or value to the clipboard.
- If supported, add context menu actions for:
  - copy code
  - copy title
  - copy full JSON
  - open URL when a URL exists

## Configuration

Add a simple config only if useful.

Config options may include:

- max results
- enabled datasets
- default copy field
- searchable fields
- reload behavior

## Resolved technical decisions

Verified against the Flow Launcher SDK in the sibling `../Flow.Launcher` checkout
(`Flow.Launcher.Plugin` 5.3.0) and the Calculator reference plugin:

- **TFM:** `net9.0-windows` with `<UseWPF>true</UseWPF>` (the SDK's `Result` exposes WPF
  types) and `<EnableDynamicLoading>true</EnableDynamicLoading>`. Reference the SDK via
  `PackageReference Flow.Launcher.Plugin 5.3.0` with `ExcludeAssets="runtime"` so the
  host-loaded copy is used (avoids duplicate-type load failures).
- **`plugin.json`:** `ID` (32-char hex GUID), `ActionKeyword` (`lu`), `Name`,
  `Description`, `Author`, `Version`, `Language` (`"csharp"`), `Website`,
  `ExecuteFileName` (`Lookup.dll`), `IcoPath` (`Images\icon.png`).
- **Clipboard / URL:** `context.API.CopyToClipboard(text)` and `context.API.OpenUrl(url)`
  (no direct `System.Windows.Clipboard`).
- **Plugin model:** `IPlugin` (sync `Init` + `Query`). The in-memory scan is
  sub-millisecond for thousands of records, so `IAsyncPlugin`/cancellation is
  unnecessary; Flow already runs `Query` inside a cancellable `Task`. Also implements
  `IContextMenu` and `IReloadable`.

Build output is flat at `Lookup\bin\<Config>\` (TFM subfolder suppressed).

## Verification

Before finalizing, test or reason through:

- `lu 541511`
- `lu 541`
- `lu software`
- `lu computer systems`
- typo search such as `lu sofware`
- empty query
- missing `data` directory
- invalid JSON file
- `lu reload`
- `lu help`
- `lu datasets`
- multiple datasets loaded at once

## Deliverables

- Complete C# Flow Launcher plugin source code.
- Working `plugin.json`.
- Sample `naics.sample.json` with enough records to demonstrate search.
- README with installation, setup, data format, examples, and troubleshooting.
- Notes explaining where large NAICS JSON files should be placed.
- Clear explanation of any Flow Launcher API assumptions.

Do not fabricate Flow Launcher API details. If the exact C# plugin API or template is not already available in the project, inspect official Flow Launcher plugin documentation or an existing working C# plugin before implementing.

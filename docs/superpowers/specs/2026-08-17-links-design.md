# Lookup — Links: combined linking and querying

**Date:** 2026-08-17
**Status:** approved, not yet implemented

## Goal

Fold link-opening into the Lookup plugin so one plugin covers both jobs: querying
local JSON datasets (today) and opening user-defined links (new). This replaces
third-party use of the `Shortcuts` plugin, whose config was orphaned when the plugin
folder was replaced on upgrade.

A link is a named target — URL, file, folder, or application — optionally carrying a
`{q}` placeholder so it can also perform a site query.

## Decisions

| Question | Decision |
|---|---|
| Scope | Extend the existing Lookup plugin; no new repo, no shared library |
| Integration | Links are a synthetic dataset flowing through the existing `SearchIndex`/`Scorer` |
| Keyword | `go` scopes to links; `lu` continues to search everything; `q` is left to the Shortcuts plugin |
| Link power | Name + aliases + target + optional icon, with `{q}` substitution |
| Icons | Auto-detect per target, with per-entry override |
| Favicons | On by default; `offline_icons: true` disables all network use |
| Storage | `links.json` in `PluginSettingsDirectoryPath`, edited via the settings panel GUI |

## Verified API facts

Do not re-derive these; they were checked against the sibling `../Flow.Launcher/`
checkout at `dev` (commit `17184c48e`).

- `PluginMetadata.PluginSettingsDirectoryPath` (`Flow.Launcher.Plugin/PluginMetadata.cs:151`)
  is the per-plugin settings directory. It survives plugin updates; the plugin
  directory does not.
- `ImageLoader` tries a shell thumbnail, then falls back to
  `System.Drawing.Icon.ExtractAssociatedIcon` (`Flow.Launcher.Infrastructure/Image/ImageLoader.cs:330`).
  Setting `Result.IcoPath` to a local file, folder, or `.exe` therefore yields the real
  icon with no extraction code in this plugin.
- `Result` icon channels: `IcoPath`, `Glyph`/`SetGlyph(GlyphInfo)`, `Icon` delegate,
  `BadgeIcoPath`/`BadgeIcon`, and `RoundedIcon` (`Flow.Launcher.Plugin/Result.cs:147`).
  `GlyphInfo` is `record GlyphInfo(string FontFamily, string Glyph)`.

## Data model

`links.json`, snake_case per `JsonDefaults`, in the plugin settings directory:

```json
{
  "links": [
    { "name": "GitHub",     "aliases": ["gh"],   "target": "https://github.com" },
    { "name": "Jira issue", "aliases": ["jira"], "target": "https://acme.atlassian.net/browse/{q}" },
    { "name": "VS Code",    "aliases": ["code"], "target": "C:\\Program Files\\Microsoft VS Code\\Code.exe" }
  ]
}
```

`LinkEntry` fields:

- `name` (required) — display title
- `aliases` (optional) — additional match keys; matching is case-insensitive
- `target` (required) — URL, file path, folder path, or executable; may contain `{q}`
- `icon` (optional) — `auto` (default), `glyph:<char>`, or an image path
- `description` (optional) — subtitle text; defaults to the target

## Components

New files under `Lookup/`:

- `Models/LinkEntry.cs` — the record above
- `Services/LinkStore.cs` — load, validate, and save `links.json`; per-entry errors
  collected rather than thrown
- `Services/LinkProjector.cs` — `LinkEntry` → `LookupItem` tagged `dataset: "links"`,
  so links enter the existing index unchanged
- `Services/IconResolver.cs` — pure function `LinkEntry → IconSpec`; no I/O, fully testable
- `Services/FaviconCache.cs` — async fetch and disk cache; the only network user
- `Services/QueryParser.cs` — longest-alias split producing `(alias, remainder)`
- `LinksEditor.cs` — settings-panel section

Modified:

- `Main.cs` — register the links source; Enter opens links (datasets keep copy-on-Enter);
  context menu for links
- `Services/Scorer.cs` — alias pinning rule (below)
- `Services/PluginConfig.cs` — `go` → `links` in `KeywordDatasets` defaults; add
  `offline_icons`
- `Lookup/plugin.json` — add `go` to `ActionKeywords`
- `Lookup.Tests/Lookup.Tests.csproj` — add every new `Services/*.cs` to `<Compile Include>`
  (required by the SDK-free test setup)

## Search and ranking

Links are indexed exactly like dataset records, so `lu <text>` returns links and
dataset hits together and `go <text>` scopes to links via the existing
`KeywordDatasets` routing.

One rule is added to `Scorer`, because thousands of NAICS rows would otherwise
outrank a two-letter link alias:

1. Exact match on a link's name or alias — pin above all fuzzy results
2. Prefix match on a link's name or alias — sort above fuzzy dataset hits
3. Otherwise — existing tiered scoring, unchanged

Dataset-only queries must score identically to today. `lu construction` is a
regression test, not a judgement call.

## Parameterized links

`QueryParser` runs before the index:

- Split the query into `(alias, remainder)` by longest matching alias
- If the matched link's target contains `{q}`, substitute the remainder,
  URL-encoding it when the target is a URL
- If the target contains `{q}` and the remainder is empty, show the entry with a
  "type a value…" subtitle and make Enter a no-op rather than opening a broken URL
- Targets without `{q}` ignore any remainder

## Icons

`IconResolver` returns an `IconSpec` describing what to render; `Main` translates that
into `Result` fields. Decision order:

1. Explicit `icon` override — glyph or image path, used as-is
2. Target is a local path — `IcoPath = target`; Flow extracts the real icon
3. Target is a URL and favicons are enabled — cached favicon if present, otherwise a
   glyph now and an async fetch that populates the cache for next time
4. Otherwise — a Segoe Fluent Icons glyph

Favicons render with `RoundedIcon = true`; site favicons are square and read as
misaligned next to Flow's other icons otherwise.

Cache location: `<PluginSettingsDirectoryPath>\icons\<host>.<ext>`, where the extension
is sniffed from the response's magic bytes (`.png`, `.ico`, `.jpg`, `.gif`, `.bmp` — the
formats Flow's `ImageLoader` accepts). Bytes are stored raw: the SDK-free core cannot
decode images, and it does not need to. A body that is not a recognised image, such as
an HTML error page, is discarded rather than cached.

Favicons are fetched first-party, from `https://<host>/favicon.ico` — the site the link
already points at — never from a third-party favicon service that would learn which
links the user opens.

A failed fetch falls back to a glyph and the host is not retried for the rest of the
session: results re-resolve icons on every keystroke, so one dead host would otherwise
become a request storm.

`offline_icons: true` in `config.json` disables step 3 entirely, restoring the
plugin's no-network guarantee.

## Settings UI

A "Links" section appended to the existing hand-built WPF `SettingsPanel` — no XAML,
matching the current `StackPanel`/`ComboBox`/`Button` style:

- Row list of links with add, edit, and delete
- Browse button for file and folder targets
- Searchable glyph picker over a curated Segoe Fluent Icons subset
- Inline validation errors from `LinkStore`

## Error handling

Follows the precedent in `PluginConfig`: a broken config never breaks startup.

- Malformed `links.json` — load every entry that parses, list the rest as errors in
  the settings panel, and keep datasets working
- Missing `links.json` — treated as an empty link list, not an error
- Unreachable favicon — glyph fallback, logged once
- Target that no longer exists — the result still appears; the open action reports
  the failure through the plugin API rather than throwing

## Testing

xUnit in `Lookup.Tests`, matching the existing SDK-free core approach:

- `LinkStore` — round-trip, malformed file recovery, missing file, duplicate aliases
- `LinkProjector` — field mapping and dataset tagging
- `QueryParser` — longest-alias wins, empty remainder, no-alias input, `{q}`-free targets
- `Scorer` — exact and prefix pinning, and a regression test that dataset-only
  ranking is unchanged
- `IconResolver` — the full decision table as a pure function

Out of scope for tests: WPF panel rendering and live favicon fetching.

## Out of scope

- Multi-target link groups (open several targets at once)
- Importing existing `Shortcuts` plugin config
- Browser bookmark or filesystem sources
- An `ISearchSource` provider abstraction — revisit only when a third source appears
- Moving the shipped `data/` datasets out of the plugin folder; only user links get the
  update-proof location in this change

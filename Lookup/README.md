# Lookup — Flow Launcher plugin

Fuzzy-search large local JSON lookup datasets straight from Flow Launcher, using the
action keyword **`lu`** (all datasets) or **`na`** (NAICS only). Ships with the full
WHD NAICS business-classification list (~2,400 codes) and is generic enough to drop
in any other dataset.

```
lu 541511          → 541511 - Custom Computer Programming Services
lu 541             → everything under code 541
lu 54-1511         → separators in codes are ignored ("541 511" works too)
lu software        → keyword search
lu computer systems→ multi-word phrase
lu sofware         → typo-tolerant (still finds "software")
na software        → same search, restricted to the NAICS dataset
```

Press **Enter** to copy the code. Open the **context menu** (Shift+Enter / right-click)
for *copy code*, *copy title*, *copy full JSON*, and *open URL* (when a record has one).

---

## Install

### From a release / build output

1. Build the plugin (see **Build** below) or download a release.
2. Copy the build output folder into Flow Launcher's plugins directory:
   `%APPDATA%\FlowLauncher\Plugins\Lookup\`
3. Restart Flow Launcher (or run `Flow Launcher: Restart`).
4. Confirm it loaded: type `lu datasets`.

The `build.ps1` script in the repo root does the build-and-copy for you.

### Folder layout once installed

```
%APPDATA%\FlowLauncher\Plugins\Lookup\
  Lookup.dll
  plugin.json
  Images\icon.png
  data\naics.json            ← datasets live here
  config.json                ← optional (see Configuration)
```

---

## Built-in commands

| Command       | Action                                                        |
|---------------|---------------------------------------------------------------|
| `lu <query>`  | Search all loaded datasets                                     |
| `na <query>`  | Search the NAICS dataset only                                  |
| `zip <query>` | Search the ZIP-code dataset only                               |
| `lu help`     | Show usage examples                                            |
| `lu datasets` | List loaded datasets, item counts, and any file-load problems |
| `lu reload`   | Show a reload command — press **Enter** to re-read the data   |

Reload is also wired to Flow's built-in **Reload Plugin Data** command.

### Keyword notes

- Scoped keywords are driven by `keyword_datasets` in `config.json` (defaults:
  `na` → `naics`, `zip` → `zipcodes`). Keywords not in the map — including `lu` —
  search everything. If you rename a keyword in Flow's plugin settings, mirror the
  rename in the map (e.g. `"keyword_datasets": { "nc": "naics", "zip": "zipcodes" }`).
- **Upgrading an existing install:** Flow Launcher remembers the keywords a plugin was
  first installed with, so a new keyword in `plugin.json` does not appear automatically.
  Add it under *Settings → Plugins → Lookup → Action keywords* (or delete the plugin
  and reinstall).

---

## Data format

Every file in the `data` folder ending in `.json` is loaded at startup. Each file is
one dataset:

```json
{
  "dataset": "naics",
  "version": "2022",
  "items": [
    {
      "id": "naics:541511",
      "code": "541511",
      "title": "Custom Computer Programming Services",
      "description": "Establishments primarily engaged in writing, modifying, testing, and supporting software.",
      "category": "Professional, Scientific, and Technical Services",
      "parent_codes": ["54", "5415"],
      "keywords": ["software", "programming", "developer", "computer systems"],
      "aliases": ["software development", "custom software"],
      "url": "https://www.census.gov/naics/?input=541511&year=2022"
    }
  ]
}
```

Field notes:

- **`code`** (shown to the user, copied on Enter) and **`title`** are the only fields
  that really matter; everything else is optional and may be omitted.
- **`id`** should be unique across *all* datasets. If you omit it, the loader
  synthesizes `"<dataset>:<code>"`. If you omit `code` but provide `id`, `id` is used
  as the code.
- **`parent_codes`** are matched at low priority (a `54` search won't flood results
  with every child) and shown as hierarchy context.
- Comments and trailing commas are tolerated in the JSON files.

### Adding your own dataset

Drop another `*.json` file (same shape) into `data\`, then run `lu reload`. Multiple
datasets are searched together; `lu datasets` shows what's loaded.

### ZIP codes

`tools/ZipConverter` (repo root) converts the free
[GeoNames US postal file](https://download.geonames.org/export/zip/US.zip) into a
~3 MB `zipcodes.json` (≈41,500 ZIPs — code, "City, ST" title, county/state category):

```powershell
dotnet run --project tools\ZipConverter -- US.txt zipcodes.json geonames-2026-07
```

Drop the output into the installed plugin's `data\` folder and reload. It is a
user-added file, so reinstalls preserve it.

### Large lookup files

Each file is parsed once at startup and held in memory; a few thousand records search
in well under a millisecond, and a ~41,500-ZIP dataset is still instant.
Files of hundreds of thousands of rows will still work but would benefit from an
inverted index (not currently implemented). The shipped `naics.json` was converted
from the WHD NAICS export kept at `tools/naics-whd-source.json`.

---

## Configuration (optional)

Create `config.json` next to `Lookup.dll`. All fields are optional:

```json
{
  "max_results": 15,
  "enabled_datasets": ["naics"],
  "default_copy_field": "code",
  "keyword_datasets": { "na": "naics", "zip": "zipcodes" }
}
```

- **`max_results`** — visible result cap (1–50, default 15).
- **`enabled_datasets`** — only load these dataset names; omit/empty = all.
- **`default_copy_field`** — `"code"` (default) or `"title"`; what Enter copies.
- **`keyword_datasets`** — maps scoped action keywords to a dataset name each one
  searches. Omit for the defaults shown; an explicit empty map `{}` disables scoping.

A missing or malformed `config.json` is ignored — defaults always apply.

---

## How ranking works

Results are scored in fixed tiers, strongest field first:

```
exact code  >  code prefix  >  exact title/alias/keyword  >  title prefix
            >  phrase (substring)  >  all query words present
            >  category / description  >  parent code  >  typo (fuzzy)
```

Pure-numeric queries (e.g. `541`) are treated as code lookups only, so numbers buried
in descriptions never create noise. Typo tolerance uses normalized Levenshtein
similarity and only kicks in for reasonably close matches. See `Services/Scorer.cs` for
the exact tiers and bonuses.

---

## Build

Requires the .NET SDK (the plugin targets `net9.0-windows`, matching the Flow Launcher
host runtime).

```powershell
dotnet build Lookup\Lookup.csproj -c Release
# output: Lookup\bin\Release\   (flat — Lookup.dll, plugin.json, Images\, data\)
```

Run the tests (pure-logic core, no Flow Launcher dependency):

```powershell
dotnet test Lookup.Tests\Lookup.Tests.csproj
```

`build.ps1` builds Release and copies the output into your Flow Launcher plugins folder.
Your installed `config.json` and any dataset files you added to `data\` are preserved;
shipped sample files are refreshed on each install, so don't edit samples in place —
copy them under a new name first.

---

## Troubleshooting

- **`lu` does nothing / plugin missing** — confirm the folder is at
  `%APPDATA%\FlowLauncher\Plugins\Lookup\` and contains `plugin.json` + `Lookup.dll`,
  then restart Flow Launcher.
- **"Lookup has no data"** — the `data` folder is missing or empty, or every file
  failed to parse. Run `lu datasets` to see per-file errors, fix them, then `lu reload`.
- **A dataset is missing** — check `lu datasets` for a parse error (invalid JSON, no
  items). Errors are listed with the file name.
- **Edited a data file but results are stale** — run `lu reload` (data is cached in
  memory; it is not re-read per keystroke).
- **"Failed to copy" toast on Enter** — the Windows clipboard was locked by another
  app (clipboard history / sync tools). The plugin retries with the lock-tolerant
  `SetText` path itself; if you still see this, it came from Flow's own copy fallback —
  upgrading Flow Launcher past 2.1.3 picks up the upstream fix.

---

## Flow Launcher API assumptions

Verified against the Flow Launcher plugin SDK (`Flow.Launcher.Plugin` 5.3.0):

- Implements `IPlugin` (`Init` + `Query`), `IContextMenu` (`LoadContextMenus`), and
  `IReloadable` (`ReloadData`).
- Uses `query.Search` (query text without the action keyword),
  `context.CurrentPluginMetadata.PluginDirectory` to locate `data\`, and
  `context.API.{CopyToClipboard, OpenUrl, LogException}`.
- `Result.ContextData` carries the matched record into the context menu.
- Icon path is relative (`Images\icon.png`); Flow resolves it against the plugin folder.

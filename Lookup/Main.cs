using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Flow.Launcher.Plugin;
using Lookup.Models;
using Lookup.Services;

namespace Lookup;

/// <summary>
/// Flow Launcher entry point for the Lookup plugin (action keywords: <c>lu</c> = all datasets,
/// second keyword — <c>na</c> by default — = NAICS only).
///
/// Implements:
/// <list type="bullet">
///   <item><see cref="IPlugin"/> — synchronous init + query. The in-memory scan is
///   sub-millisecond for thousands of records, so the async/cancellation model is
///   unnecessary; Flow already runs <see cref="Query"/> inside a cancellable Task.</item>
///   <item><see cref="IContextMenu"/> — per-result copy/open actions.</item>
///   <item><see cref="IReloadable"/> — powers Flow's built-in "Reload Plugin Data".</item>
///   <item><see cref="ISettingProvider"/> — the panel in Flow's plugin settings.</item>
/// </list>
/// </summary>
public class Main : IPlugin, IContextMenu, IReloadable, ISettingProvider
{
    private const string IconPath = "Images\\icon.png";
    /// <summary>Bullet for dataset record rows; the magnifier stays on the plugin's
    /// own rows (help, commands, dataset list, errors).</summary>
    private const string DotIconPath = "Images\\dot.png";
    private const string ClassName = nameof(Main);

    /// <summary>Pins sub-command results above appended search hits — the scorer's
    /// ceiling is ExactCode + 200 (10,200), so anything above that always leads.
    /// Only used under the plugin's own keyword, where no other plugin competes.</summary>
    private const int CommandScore = 100_000;

    /// <summary>Command-row base score. A global ('*') install shares the result list
    /// with every other plugin, where the 100k pin would hijack Flow's ranking for
    /// anyone typing "help"/"datasets"/"reload" — stay modest there.</summary>
    private static int CommandBase(string? typedKw) =>
        string.IsNullOrEmpty(typedKw) || typedKw == "*" ? 100 : CommandScore;

    private static readonly JsonSerializerOptions PrettyJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private PluginInitContext _context = null!;
    private PluginConfig _config = new();
    private readonly SearchIndex _index = new();
    private List<LoadError> _loadErrors = new();
    /// <summary>Every dataset found on disk, before the enabled_datasets filter —
    /// the settings panel needs disabled ones too, which _index never sees.</summary>
    private List<DatasetInfo> _availableDatasets = new();

    /// <summary>User links, projected into the shared index. Rebuilt on reload.</summary>
    private List<LinkEntry> _linkEntries = new();
    private LinkProjection _linkProjection = LinkProjector.Project(Array.Empty<LinkEntry>());
    private List<LinkError> _linkErrors = new();
    private FaviconCache? _favicons;

    public void Init(PluginInitContext context)
    {
        _context = context;
        LoadData();
    }

    /// <summary>Loads config + datasets and rebuilds the index. Used by Init and reload.</summary>
    private void LoadData()
    {
        try
        {
            var pluginDir = _context.CurrentPluginMetadata.PluginDirectory;
            _config = PluginConfig.Load(pluginDir);

            var load = DataLoader.Load(Path.Combine(pluginDir, "data"));
            _loadErrors = load.Errors;
            _availableDatasets = load.Datasets
                .Select(d => new DatasetInfo(d.Dataset, d.Version, d.Items.Count))
                .ToList();

            // Links live in the settings directory, not the plugin folder: a plugin
            // update replaces the latter and would take the user's links with it.
            var settingsDir = _context.CurrentPluginMetadata.PluginSettingsDirectoryPath;
            _favicons ??= new FaviconCache(settingsDir);
            var links = LinkStore.Load(settingsDir);
            _linkErrors = links.Errors;
            _linkEntries = links.Links;
            _linkProjection = LinkProjector.Project(links.Links);

            var datasets = load.Datasets.Append(_linkProjection.Dataset);
            _index.Build(datasets, EnabledDatasetsIncludingLinks());
        }
        catch (Exception ex)
        {
            _loadErrors = new List<LoadError> { new("(startup)", ex.Message) };
            _context.API.LogException(ClassName, "Failed to load lookup data", ex);
        }
    }

    /// <summary>The enabled_datasets filter is about data files; links are a user's own
    /// entries and are never filtered out by it.</summary>
    private List<string>? EnabledDatasetsIncludingLinks()
    {
        if (_config.EnabledDatasets is not { Count: > 0 } enabled)
            return null; // null means "everything", links included

        var withLinks = new List<string>(enabled);
        if (!withLinks.Contains(LinkProjector.DatasetName, StringComparer.OrdinalIgnoreCase))
            withLinks.Add(LinkProjector.DatasetName);
        return withLinks;
    }

    /// <summary>Called by Flow's "Reload Plugin Data" command.</summary>
    public void ReloadData() => LoadData();

    /// <summary>Panel shown in Flow's Settings → Plugins → Lookup.</summary>
    public System.Windows.Controls.Control CreateSettingPanel() =>
        SettingsPanel.Build(
            _context,
            () => _config,
            _index,
            () => _loadErrors,
            () => _availableDatasets,
            ReloadData,
            () => _config.Save(_context.CurrentPluginMetadata.PluginDirectory),
            () => _linkEntries,
            () => _linkErrors,
            links => LinkStore.Save(_context.CurrentPluginMetadata.PluginSettingsDirectoryPath, links));

    public List<Result> Query(Query query)
    {
        var search = (query.Search ?? string.Empty).Trim();
        var typedKw = query.ActionKeyword;   // "" when the user made the plugin global via '*'
        var kw = DisplayKeyword(typedKw);    // keyword for human-readable hints
        var datasetFilter = DatasetFilterFor(typedKw);

        // ---- Sub-commands (exact match only, so e.g. "help desk" still searches).
        // Real search hits are appended below the command results, so a record that is
        // literally titled "help", "datasets" or "reload" stays reachable. ----
        switch (search.ToLowerInvariant())
        {
            case "help":
                return WithSearchHits(HelpResults(typedKw), search, datasetFilter, typedKw);
            case "datasets":
                return WithSearchHits(DatasetResults(kw, CommandBase(typedKw)), search, datasetFilter, typedKw);
            case "reload":
                return WithSearchHits(new List<Result> { ReloadCommand(kw, CommandBase(typedKw)) }, search, datasetFilter, typedKw);
        }

        // ---- Empty query: list the links under a links-scoped keyword, guidance elsewhere.
        // The index needs query text to score against, so an unfiltered listing is the
        // one case that bypasses it. ----
        if (search.Length == 0)
        {
            return IsLinksScope(datasetFilter)
                ? AllLinkResults(typedKw)
                : HelpResults(typedKw);
        }

        // ---- Hard failure: nothing loaded ----
        if (_index.Count == 0)
        {
            var errors = _loadErrors; // snapshot: ReloadData swaps the list on another thread
            var msg = errors.Count > 0 ? errors[0].Message : "No datasets are loaded.";
            return new List<Result>
            {
                Info("Lookup has no data", $"{msg}   Fix the data folder, then type  {kw} reload .")
            };
        }

        // ---- Scoped keyword whose dataset isn't loaded: say so, instead of every
        // query dying as a misleading "no close matches". ----
        if (datasetFilter is not null && !DatasetLoaded(datasetFilter))
        {
            return new List<Result>
            {
                Info($"The “{datasetFilter}” dataset is not loaded",
                    $"Check the data folder and enabled_datasets in config.json, then type  {kw} reload .")
            };
        }

        // ---- Search ----
        var hits = _index.Search(search, _config.MaxResults, datasetFilter);
        var results = hits.Select(h => ToResult(h, typedKw)).ToList();

        // A parameterised link typed with a value ("jira ABC-123") is a different action
        // from the bare link row, so it replaces that row rather than sitting beside it.
        //
        // This runs before the empty check, not after: the whole point of a {q} link is
        // that the typed value is arbitrary text, which by design matches nothing in the
        // index. Checking for "no results" first would reject every parameterised query.
        var match = QueryParser.Match(search, _linkEntries);
        if (match is not null && match.Link.HasQueryPlaceholder && match.Remainder.Length > 0)
        {
            var matchedId = IdOf(match.Link);
            results.RemoveAll(r => r.ContextData is LookupItem existing && existing.Id == matchedId);
            results.Insert(0, ToLinkResult(match.Link, matchedId, CommandBase(typedKw), typedKw, match.Remainder));
        }

        if (results.Count == 0)
        {
            return new List<Result>
            {
                Info("No close matches found", $"Nothing matched “{search}”. Try fewer or different words.")
            };
        }

        return results;
    }

    /// <summary>Every link, for an empty links-scoped query.</summary>
    private List<Result> AllLinkResults(string? typedKw)
    {
        if (_linkEntries.Count == 0)
        {
            return new List<Result>
            {
                Info("No links defined yet",
                    "Add links in Flow's Settings → Plugins → Lookup, or edit links.json.")
            };
        }

        return _linkEntries
            .Select(link => ToLinkResult(link, IdOf(link), 0, typedKw, ""))
            .ToList();
    }

    private bool IsLinksScope(string? datasetFilter) =>
        string.Equals(datasetFilter, LinkProjector.DatasetName, StringComparison.OrdinalIgnoreCase);

    /// <summary>Projected item id for a link, or "" when it did not come from the
    /// current projection.</summary>
    private string IdOf(LinkEntry link)
    {
        foreach (var (id, entry) in _linkProjection.ByItemId)
            if (ReferenceEquals(entry, link))
                return id;
        return "";
    }

    public List<Result> LoadContextMenus(Result selectedResult)
    {
        if (selectedResult.ContextData is not LookupItem item)
            return new List<Result>();

        // A link's menu is about opening and copying its target; the dataset menus below
        // (copy code, copy NAICS description, copy JSON) would be meaningless for one.
        if (_linkProjection.ByItemId.TryGetValue(item.Id, out var link))
            return LinkContextMenus(link);

        var menus = new List<Result>();

        // Code/title can each be empty (records need only one of the two) — offering a
        // copy of an empty string would silently no-op in Flow's clipboard API.
        if (!string.IsNullOrEmpty(item.Code))
            menus.Add(Menu("Copy code", item.Code, () => ClipboardHelper.Copy(item.Code, _context.API)));
        if (!string.IsNullOrEmpty(item.Title))
            menus.Add(Menu("Copy title", item.Title, () => ClipboardHelper.Copy(item.Title, _context.API)));

        // Combined formats only earn their own row when they'd differ from the two
        // single-field rows above — a code-only or title-only record already has that
        // single value covered, so a redundant "code - title" entry would just repeat it.
        if (!string.IsNullOrEmpty(item.Code) && !string.IsNullOrEmpty(item.Title))
        {
            var codeTitle = CopyFormatter.CodeTitle(item);
            menus.Add(Menu("Copy code - title", codeTitle, () => ClipboardHelper.Copy(codeTitle, _context.API)));
        }
        if (!string.IsNullOrWhiteSpace(item.Description))
        {
            var full = CopyFormatter.FullDetails(item);
            menus.Add(Menu("Copy full details", "Code, title, and the full NAICS description", () => ClipboardHelper.Copy(full, _context.API)));
        }

        menus.Add(Menu("Copy full JSON", "Copy this record as JSON", () => ClipboardHelper.Copy(ToJson(item), _context.API)));

        if (!string.IsNullOrWhiteSpace(item.Url))
            menus.Add(Menu("Open URL", item.Url, () => _context.API.OpenUrl(item.Url)));

        return menus;
    }

    // --- keyword helpers ------------------------------------------------------

    /// <summary>Keyword shown in hints: the typed one when present, else the first
    /// configured keyword. Never hardcoded, so hints stay correct after the user
    /// renames keywords in Flow's settings.</summary>
    private string DisplayKeyword(string? typed)
    {
        if (!string.IsNullOrEmpty(typed) && typed != "*") return typed;
        var kws = _context.CurrentPluginMetadata.ActionKeywords;
        return kws?.FirstOrDefault(k => !string.IsNullOrEmpty(k) && k != "*") ?? "lu";
    }

    /// <summary>Prefixes a value with the typed keyword for AutoCompleteText; a global
    /// ('*') plugin has no keyword, so the value stands alone.</summary>
    private static string JoinKeyword(string? typed, string value) =>
        string.IsNullOrEmpty(typed) || typed == "*" ? value : $"{typed} {value}";

    /// <summary>Scoped action keywords restrict the search to one dataset, driven by
    /// the config's keyword_datasets map (defaults: na → naics, zip → zipcodes).
    /// Unmapped keywords search everything.</summary>
    private string? DatasetFilterFor(string? typed)
    {
        if (string.IsNullOrEmpty(typed) || typed == "*") return null;
        return _config.KeywordDatasets.TryGetValue(typed, out var dataset) ? dataset : null;
    }

    private bool DatasetLoaded(string name) =>
        _index.Datasets.Any(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase));

    // --- result construction -------------------------------------------------

    private Result ToResult(ScoredRecord hit, string? typedKw)
    {
        var item = hit.Record.Item;

        // Links render and act differently: Enter opens rather than copies, and the
        // title is the link's name, not the dataset's "{code} - {title}" form.
        if (string.Equals(hit.Record.Dataset, LinkProjector.DatasetName, StringComparison.OrdinalIgnoreCase) &&
            _linkProjection.ByItemId.TryGetValue(item.Id, out var link))
            return ToLinkResult(link, item.Id, hit.Score, typedKw, "");

        var title = CopyFormatter.CodeTitle(item);
        var subtitle = BuildSubtitle(item);
        var copyValue = CopyValue(item);

        // Highlights are computed against the bare title; shift them past the "{code} - "
        // prefix so the bolded characters land on the matched word in the displayed title.
        var highlight = hit.TitleHighlight;
        if (!string.IsNullOrEmpty(item.Code) && highlight.Count > 0)
        {
            var offset = item.Code.Length + 3; // length of "{code} - "
            highlight = highlight.Select(i => i + offset).ToList();
        }

        // Code-less records are legal (title-only), so Tab completes to the title
        // instead of wiping the typed query with a bare keyword.
        var completeValue = string.IsNullOrEmpty(item.Code) ? item.Title : item.Code;

        return new Result
        {
            Title = title,
            SubTitle = subtitle,
            IcoPath = DotIconPath,
            Score = hit.Score,
            CopyText = copyValue,
            TitleHighlightData = highlight,
            TitleToolTip = title,
            SubTitleToolTip = subtitle,
            ContextData = item,
            AutoCompleteText = JoinKeyword(typedKw, completeValue),
            Action = _ =>
            {
                ClipboardHelper.Copy(copyValue, _context.API);
                return true; // hide Flow after copying
            },
        };
    }

    /// <summary>Builds the row for a link. <paramref name="remainder"/> is the text typed
    /// after the alias, substituted into a {q} target.</summary>
    private Result ToLinkResult(LinkEntry link, string itemId, int score, string? typedKw, string remainder)
    {
        var needsValue = QueryParser.NeedsParameter(link, remainder);
        var target = QueryParser.BuildTarget(link, remainder);

        var subtitle = needsValue
            ? $"Type a value after “{link.Aliases.FirstOrDefault() ?? link.Name}” to open this link"
            : link.Description.Length > 0 && remainder.Length == 0 ? link.Description : target;

        // Resolved before construction: Result.Glyph is init-only in the plugin SDK.
        var icon = ResolveIcon(link);

        var result = new Result
        {
            Title = link.Name,
            SubTitle = subtitle,
            Score = score,
            IcoPath = icon.Kind == IconKind.Image ? icon.Value : IconPath,
            RoundedIcon = icon.Kind == IconKind.Image && icon.Rounded,
            Glyph = icon.Kind == IconKind.Glyph ? new GlyphInfo(IconResolver.FontFamily, icon.Value) : null,
            TitleToolTip = link.Name,
            SubTitleToolTip = target,
            CopyText = target,
            ContextData = ItemFor(itemId),
            AutoCompleteText = JoinKeyword(typedKw, link.Aliases.FirstOrDefault() ?? link.Name),
            Action = _ =>
            {
                if (needsValue)
                {
                    // Nothing to open yet: keep Flow open so the user can finish typing.
                    _context.API.ShowMsg("Lookup", subtitle);
                    return false;
                }

                return OpenTarget(target);
            },
        };

        return result;
    }

    private List<Result> LinkContextMenus(LinkEntry link)
    {
        var menus = new List<Result>();

        if (!link.HasQueryPlaceholder)
            menus.Add(Menu("Open", link.Target, () => OpenTarget(link.Target)));

        menus.Add(Menu("Copy target", link.Target, () => ClipboardHelper.Copy(link.Target, _context.API)));
        menus.Add(Menu("Copy name", link.Name, () => ClipboardHelper.Copy(link.Name, _context.API)));

        if (link.Aliases.Count > 0)
        {
            var aliases = string.Join(", ", link.Aliases);
            menus.Add(Menu("Copy aliases", aliases, () => ClipboardHelper.Copy(aliases, _context.API)));
        }

        return menus;
    }

    /// <summary>The projected LookupItem for a link id, used as ContextData so the
    /// context menu can find its way back to the link.</summary>
    private LookupItem? ItemFor(string itemId) =>
        _linkProjection.Dataset.Items.FirstOrDefault(i => i.Id == itemId);

    /// <summary>Resolves a link's icon and, when it is a favicon that has not been
    /// fetched yet, starts that fetch in the background.</summary>
    private IconSpec ResolveIcon(LinkEntry link)
    {
        var cleanTarget = IconResolver.StripPlaceholder(link.Target);
        var cached = _config.OfflineIcons ? null : _favicons?.TryGetCached(cleanTarget);
        var spec = IconResolver.Resolve(link, !_config.OfflineIcons, cached);

        if (spec.FetchUrl is { Length: > 0 } fetchUrl && _favicons is not null)
        {
            // Fire and forget: Query runs per keystroke and must not wait on the network.
            // EnsureAsync absorbs its own failures and never retries a dead host.
            _ = Task.Run(() => _favicons.EnsureAsync(fetchUrl, CancellationToken.None));
        }

        return spec;
    }

    /// <summary>Opens a link target. Returns true to let Flow hide itself.</summary>
    private bool OpenTarget(string target)
    {
        try
        {
            if (target.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                target.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                _context.API.OpenUrl(target);
                return true;
            }

            var expanded = Environment.ExpandEnvironmentVariables(target);

            if (Directory.Exists(expanded))
            {
                _context.API.OpenDirectory(expanded);
                return true;
            }

            // Files, executables and custom schemes: let the shell decide what "open" means.
            using var process = System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(expanded) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            _context.API.LogException(ClassName, $"Failed to open link target: {target}", ex);
            _context.API.ShowMsgError("Could not open this link", ex.Message);
            return false; // keep Flow open so the user can see what failed
        }
    }

    private static string BuildSubtitle(LookupItem item)
    {
        var parts = new List<string>(2);
        if (!string.IsNullOrWhiteSpace(item.Category)) parts.Add(item.Category);
        if (!string.IsNullOrWhiteSpace(item.Description)) parts.Add(item.Description);
        else if (item.Keywords.Count > 0) parts.Add(string.Join(", ", item.Keywords.Take(6)));
        return parts.Count > 0 ? string.Join("  ·  ", parts) : "";
    }

    /// <summary>Value copied on Enter. Falls back to the other field when the preferred
    /// one is empty — the loader guarantees at least one of code/title is set, so the
    /// copy is never a silent empty-string no-op.</summary>
    private string CopyValue(LookupItem item)
    {
        var preferTitle = string.Equals(_config.DefaultCopyField, "title", StringComparison.OrdinalIgnoreCase);
        var primary = preferTitle ? item.Title : item.Code;
        return string.IsNullOrEmpty(primary) ? (preferTitle ? item.Code : item.Title) : primary;
    }

    private static string ToJson(LookupItem item) => JsonSerializer.Serialize(item, PrettyJson);

    // --- sub-command result builders ----------------------------------------

    /// <summary>Appends real search hits below sub-command results so records that
    /// happen to be titled "help", "datasets" or "reload" remain reachable.</summary>
    private List<Result> WithSearchHits(List<Result> commands, string search, string? datasetFilter, string? typedKw)
    {
        var hits = _index.Search(search, _config.MaxResults, datasetFilter);
        commands.AddRange(hits.Select(h => ToResult(h, typedKw)));
        return commands;
    }

    /// <summary>'reload' is Enter-gated: Query fires on every keystroke, so reloading
    /// inline would re-read every dataset file while the user is still typing.</summary>
    private Result ReloadCommand(string kw, int score) => new()
    {
        Title = "Reload lookup data",
        SubTitle = "Press Enter to re-read the data folder and rebuild the index.",
        IcoPath = IconPath,
        Score = score,
        Action = _ =>
        {
            // Result actions run on Flow's UI thread — push the disk re-read off it.
            // ShowMsg needs an absolute icon path (relative resolves against Flow's
            // own directory, silently falling back to the default icon), and it
            // dispatches to the UI thread internally, so it is background-safe.
            var icon = Path.Combine(_context.CurrentPluginMetadata.PluginDirectory, IconPath);
            System.Threading.Tasks.Task.Run(() =>
            {
                ReloadData();
                _context.API.ShowMsg("Lookup reloaded",
                    $"{_index.Count} item(s) across {_index.Datasets.Count} dataset(s){ErrorSuffix(kw)}", icon);
            });
            return true;
        },
    };

    private List<Result> HelpResults(string? typedKw)
    {
        var examples = new (string Query, string Desc)[]
        {
            ("541511",          "Exact code match"),
            ("541",             "Code prefix — everything under 541"),
            ("software",        "Keyword search"),
            ("computer systems","Multi-word phrase"),
            ("sofware",         "Typo-tolerant search"),
            ("datasets",        "List loaded datasets and item counts"),
            ("reload",          "Reload JSON data without restarting Flow"),
            ("help",            "Show these examples"),
        };

        var baseScore = CommandBase(typedKw);
        return examples.Select((e, i) => new Result
        {
            Title = JoinKeyword(typedKw, e.Query),
            SubTitle = e.Desc,
            IcoPath = IconPath,
            Score = baseScore - i, // descending: keeps the examples in listed order
            AutoCompleteText = JoinKeyword(typedKw, e.Query),
            Action = _ => false, // keep Flow open; Tab autocompletes the example
        }).ToList();
    }

    private List<Result> DatasetResults(string kw, int baseScore)
    {
        var list = new List<Result>();
        var score = baseScore;

        foreach (var d in _index.Datasets)
        {
            var version = string.IsNullOrWhiteSpace(d.Version) ? "" : $"  (v{d.Version})";
            list.Add(new Result
            {
                Title = $"{d.Name}{version}",
                SubTitle = $"{d.Count} item(s)",
                IcoPath = IconPath,
                Score = score--,
            });
        }

        // Same descending counter as the rows above, so the guidance line stays on
        // top and file warnings follow beneath it.
        if (list.Count == 0)
            list.Add(new Result
            {
                Title = "No datasets loaded",
                SubTitle = $"Put dataset .json files in the plugin's  data  folder, then type  {kw} reload .",
                IcoPath = IconPath,
                Score = score--,
            });

        var errors = _loadErrors; // snapshot: ReloadData swaps the list on another thread
        foreach (var e in errors)
            list.Add(new Result
            {
                Title = $"⚠ {Path.GetFileName(e.File)}",
                SubTitle = e.Message,
                IcoPath = IconPath,
                Score = score--,
            });

        return list;
    }

    // --- small result helpers ------------------------------------------------

    private string ErrorSuffix(string kw)
    {
        var errors = _loadErrors; // snapshot: ReloadData swaps the list on another thread
        return errors.Count == 0 ? "" : $"   ({errors.Count} file issue(s) — see  {kw} datasets )";
    }

    private static Result Info(string title, string subtitle) => new()
    {
        Title = title, SubTitle = subtitle, IcoPath = IconPath, Score = 100,
    };

    private static Result Menu(string title, string subtitle, Action action) => new()
    {
        Title = title,
        SubTitle = subtitle,
        IcoPath = IconPath,
        Action = _ =>
        {
            action();
            return true;
        },
    };
}

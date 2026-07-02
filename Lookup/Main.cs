using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
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
/// </list>
/// </summary>
public class Main : IPlugin, IContextMenu, IReloadable
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
            _index.Build(load.Datasets, _config.EnabledDatasets);
        }
        catch (Exception ex)
        {
            _loadErrors = new List<LoadError> { new("(startup)", ex.Message) };
            _context.API.LogException(ClassName, "Failed to load lookup data", ex);
        }
    }

    /// <summary>Called by Flow's "Reload Plugin Data" command.</summary>
    public void ReloadData() => LoadData();

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

        // ---- Empty query: show guidance ----
        if (search.Length == 0) return HelpResults(typedKw);

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
        if (hits.Count == 0)
        {
            return new List<Result>
            {
                Info("No close matches found", $"Nothing matched “{search}”. Try fewer or different words.")
            };
        }

        return hits.Select(h => ToResult(h, typedKw)).ToList();
    }

    public List<Result> LoadContextMenus(Result selectedResult)
    {
        if (selectedResult.ContextData is not LookupItem item)
            return new List<Result>();

        var menus = new List<Result>();

        // Code/title can each be empty (records need only one of the two) — offering a
        // copy of an empty string would silently no-op in Flow's clipboard API.
        if (!string.IsNullOrEmpty(item.Code))
            menus.Add(Menu("Copy code", item.Code, () => _context.API.CopyToClipboard(item.Code)));
        if (!string.IsNullOrEmpty(item.Title))
            menus.Add(Menu("Copy title", item.Title, () => _context.API.CopyToClipboard(item.Title)));
        menus.Add(Menu("Copy full JSON", "Copy this record as JSON", () => _context.API.CopyToClipboard(ToJson(item))));

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
        var title = string.IsNullOrEmpty(item.Code) ? item.Title : $"{item.Code} - {item.Title}";
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
                _context.API.CopyToClipboard(copyValue);
                return true; // hide Flow after copying
            },
        };
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

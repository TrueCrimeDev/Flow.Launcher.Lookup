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
/// Flow Launcher entry point for the Lookup plugin (action keyword <c>lu</c>).
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
    private const string ClassName = nameof(Main);

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

        // ---- Sub-commands (exact match only, so e.g. "help desk" still searches) ----
        switch (search.ToLowerInvariant())
        {
            case "help":
                return HelpResults();
            case "datasets":
                return DatasetResults();
            case "reload":
                ReloadData();
                return new List<Result>
                {
                    Info("Reloaded lookup data",
                        $"{_index.Count} item(s) across {_index.Datasets.Count} dataset(s){ErrorSuffix()}")
                };
        }

        // ---- Empty query: show guidance ----
        if (search.Length == 0) return HelpResults();

        // ---- Hard failure: nothing loaded ----
        if (_index.Count == 0)
        {
            var msg = _loadErrors.Count > 0 ? _loadErrors[0].Message : "No datasets are loaded.";
            return new List<Result>
            {
                Error("Lookup has no data", $"{msg}   Fix the data folder, then type  lu reload .")
            };
        }

        // ---- Search ----
        var hits = _index.Search(search, _config.MaxResults);
        if (hits.Count == 0)
        {
            return new List<Result>
            {
                Info("No close matches found", $"Nothing matched “{search}”. Try fewer or different words.")
            };
        }

        return hits.Select(ToResult).ToList();
    }

    public List<Result> LoadContextMenus(Result selectedResult)
    {
        if (selectedResult.ContextData is not LookupItem item)
            return new List<Result>();

        var menus = new List<Result>
        {
            Menu("Copy code", item.Code, () => _context.API.CopyToClipboard(item.Code)),
            Menu("Copy title", item.Title, () => _context.API.CopyToClipboard(item.Title)),
            Menu("Copy full JSON", "Copy this record as JSON", () => _context.API.CopyToClipboard(ToJson(item))),
        };

        if (!string.IsNullOrWhiteSpace(item.Url))
            menus.Add(Menu("Open URL", item.Url, () => _context.API.OpenUrl(item.Url)));

        return menus;
    }

    // --- result construction -------------------------------------------------

    private Result ToResult(ScoredRecord hit)
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

        return new Result
        {
            Title = title,
            SubTitle = subtitle,
            IcoPath = IconPath,
            Score = hit.Score,
            CopyText = copyValue,
            TitleHighlightData = highlight,
            TitleToolTip = title,
            SubTitleToolTip = subtitle,
            ContextData = item,
            AutoCompleteText = $"{_context.CurrentPluginMetadata.ActionKeyword} {item.Code}",
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

    private string CopyValue(LookupItem item) =>
        string.Equals(_config.DefaultCopyField, "title", StringComparison.OrdinalIgnoreCase)
            ? item.Title
            : item.Code;

    private static string ToJson(LookupItem item) => JsonSerializer.Serialize(item, PrettyJson);

    // --- sub-command result builders ----------------------------------------

    private List<Result> HelpResults()
    {
        // Use the live action keyword so help + Tab-autocomplete stay correct if renamed.
        var kw = _context.CurrentPluginMetadata.ActionKeyword;
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

        return examples.Select(e => new Result
        {
            Title = $"{kw} {e.Query}",
            SubTitle = e.Desc,
            IcoPath = IconPath,
            Score = 0,
            AutoCompleteText = $"{kw} {e.Query}",
            Action = _ => false, // keep Flow open; Tab autocompletes the example
        }).ToList();
    }

    private List<Result> DatasetResults()
    {
        var list = new List<Result>();

        foreach (var d in _index.Datasets)
        {
            var version = string.IsNullOrWhiteSpace(d.Version) ? "" : $"  (v{d.Version})";
            list.Add(new Result
            {
                Title = $"{d.Name}{version}",
                SubTitle = $"{d.Count} item(s)",
                IcoPath = IconPath,
                Score = 100,
            });
        }

        if (list.Count == 0)
            list.Add(Error("No datasets loaded",
                "Put dataset .json files in the plugin's  data  folder, then type  lu reload ."));

        foreach (var e in _loadErrors)
            list.Add(new Result
            {
                Title = $"⚠ {Path.GetFileName(e.File)}",
                SubTitle = e.Message,
                IcoPath = IconPath,
                Score = 50,
            });

        return list;
    }

    // --- small result helpers ------------------------------------------------

    private string ErrorSuffix() =>
        _loadErrors.Count == 0 ? "" : $"   ({_loadErrors.Count} file issue(s) — see  lu datasets )";

    private static Result Info(string title, string subtitle) => new()
    {
        Title = title, SubTitle = subtitle, IcoPath = IconPath, Score = 100,
    };

    private static Result Error(string title, string subtitle) => new()
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

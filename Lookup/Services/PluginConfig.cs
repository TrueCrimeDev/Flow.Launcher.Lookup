using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Lookup.Services;

/// <summary>
/// Optional plugin configuration, read from <c>config.json</c> in the plugin folder.
/// The file is entirely optional: sensible defaults apply when it is absent, partial,
/// or invalid (a broken config never breaks startup).
/// </summary>
public sealed class PluginConfig
{
    /// <summary>Maximum number of results shown. Clamped to 1..50; defaults to 15.</summary>
    public int MaxResults { get; set; } = 15;

    /// <summary>Dataset names to enable. Null or empty means "all loaded datasets".</summary>
    public List<string>? EnabledDatasets { get; set; }

    /// <summary>Field copied to the clipboard on Enter: "code" (default) or "title".</summary>
    public string DefaultCopyField { get; set; } = "code";

    /// <summary>Maps scoped action keywords to the dataset each one searches
    /// (config key <c>keyword_datasets</c>). Keywords not listed here — including the
    /// primary <c>lu</c> — search every dataset. Defaults cover the shipped keywords;
    /// if you rename a keyword in Flow's settings, mirror the rename here.</summary>
    public Dictionary<string, string> KeywordDatasets { get; set; } = DefaultKeywordDatasets();

    private static Dictionary<string, string> DefaultKeywordDatasets() =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["na"] = "naics",
            ["zip"] = "zipcodes",
        };

    public static PluginConfig Load(string pluginDirectory)
    {
        try
        {
            var path = Path.Combine(pluginDirectory, "config.json");
            if (!File.Exists(path)) return new PluginConfig();

            var cfg = JsonSerializer.Deserialize<PluginConfig>(File.ReadAllText(path), JsonDefaults.SnakeCase)
                      ?? new PluginConfig();

            if (cfg.MaxResults is < 1 or > 50) cfg.MaxResults = 15;
            if (string.IsNullOrWhiteSpace(cfg.DefaultCopyField)) cfg.DefaultCopyField = "code";
            // Re-wrap for case-insensitive lookup (the deserializer builds a
            // case-sensitive dictionary); null falls back to the shipped defaults,
            // an explicit empty map disables keyword scoping.
            cfg.KeywordDatasets = new Dictionary<string, string>(
                cfg.KeywordDatasets ?? DefaultKeywordDatasets(), StringComparer.OrdinalIgnoreCase);
            return cfg;
        }
        catch
        {
            return new PluginConfig();
        }
    }
}

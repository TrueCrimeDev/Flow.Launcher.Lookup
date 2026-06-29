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

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        // Match the data files: config keys are snake_case (max_results, enabled_datasets, …).
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static PluginConfig Load(string pluginDirectory)
    {
        try
        {
            var path = Path.Combine(pluginDirectory, "config.json");
            if (!File.Exists(path)) return new PluginConfig();

            var cfg = JsonSerializer.Deserialize<PluginConfig>(File.ReadAllText(path), JsonOptions)
                      ?? new PluginConfig();

            if (cfg.MaxResults is < 1 or > 50) cfg.MaxResults = 15;
            if (string.IsNullOrWhiteSpace(cfg.DefaultCopyField)) cfg.DefaultCopyField = "code";
            return cfg;
        }
        catch
        {
            return new PluginConfig();
        }
    }
}

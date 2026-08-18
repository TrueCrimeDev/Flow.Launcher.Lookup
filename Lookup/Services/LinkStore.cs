using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Lookup.Models;

namespace Lookup.Services;

/// <summary>A rejected or corrected link entry, surfaced in the settings panel.</summary>
public sealed record LinkError(string Name, string Message);

/// <summary>Outcome of a load pass: the links that survived validation, plus what went wrong.</summary>
public sealed class LinkLoadResult
{
    public List<LinkEntry> Links { get; } = new();
    public List<LinkError> Errors { get; } = new();
}

/// <summary>
/// Reads and writes <c>links.json</c> in Flow's per-plugin settings directory
/// (<c>PluginMetadata.PluginSettingsDirectoryPath</c>), which survives plugin updates —
/// unlike the plugin folder, where a competing plugin's config was orphaned on upgrade.
///
/// Like <see cref="PluginConfig"/>, nothing here throws: a missing file is an empty
/// link list, and a malformed one degrades to an error the user can see and fix.
/// </summary>
public static class LinkStore
{
    public const string FileName = "links.json";

    private static readonly JsonSerializerOptions SaveOptions = new(JsonDefaults.SnakeCase)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string FilePath(string settingsDirectory) =>
        Path.Combine(settingsDirectory ?? "", FileName);

    public static LinkLoadResult Load(string settingsDirectory)
    {
        var result = new LinkLoadResult();

        if (string.IsNullOrWhiteSpace(settingsDirectory))
            return result;

        var path = FilePath(settingsDirectory);
        if (!File.Exists(path))
            return result; // no links defined yet — not an error

        LinksFile? file;
        try
        {
            using var stream = File.OpenRead(path);
            file = JsonSerializer.Deserialize<LinksFile>(stream, JsonDefaults.SnakeCase);
        }
        catch (JsonException ex)
        {
            result.Errors.Add(new LinkError(FileName, "Invalid JSON: " + ex.Message));
            return result;
        }
        catch (Exception ex)
        {
            result.Errors.Add(new LinkError(FileName, ex.Message));
            return result;
        }

        if (file?.Links is null)
            return result;

        // Aliases must be globally unique: the query parser resolves an alias to exactly
        // one link, so a duplicate is dropped rather than silently shadowing another entry.
        var claimedAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in file.Links)
        {
            if (entry is null)
                continue;

            entry.Name = (entry.Name ?? "").Trim();
            entry.Target = (entry.Target ?? "").Trim();
            entry.Description = (entry.Description ?? "").Trim();
            entry.Icon = string.IsNullOrWhiteSpace(entry.Icon) ? "auto" : entry.Icon.Trim();

            if (entry.Name.Length == 0)
            {
                result.Errors.Add(new LinkError("(unnamed)", "Link has no name; skipped."));
                continue;
            }

            if (entry.Target.Length == 0)
            {
                result.Errors.Add(new LinkError(entry.Name, "Link has no target; skipped."));
                continue;
            }

            entry.Aliases = Clean(entry.Aliases, entry.Name, claimedAliases, result.Errors);
            result.Links.Add(entry);
        }

        return result;
    }

    public static void Save(string settingsDirectory, IEnumerable<LinkEntry> links)
    {
        if (string.IsNullOrWhiteSpace(settingsDirectory))
            throw new ArgumentException("Settings directory is required.", nameof(settingsDirectory));

        Directory.CreateDirectory(settingsDirectory);
        var payload = new LinksFile { Links = links?.ToList() ?? new List<LinkEntry>() };
        File.WriteAllText(FilePath(settingsDirectory), JsonSerializer.Serialize(payload, SaveOptions));
    }

    /// <summary>Trims blanks, drops duplicates, and records why anything was dropped.</summary>
    private static List<string> Clean(
        List<string>? aliases, string owner, HashSet<string> claimed, List<LinkError> errors)
    {
        var cleaned = new List<string>();
        if (aliases is null)
            return cleaned;

        foreach (var alias in aliases)
        {
            if (string.IsNullOrWhiteSpace(alias))
                continue;

            var trimmed = alias.Trim();
            if (!claimed.Add(trimmed))
            {
                errors.Add(new LinkError(owner, $"Alias '{trimmed}' is already used by another link; ignored."));
                continue;
            }

            cleaned.Add(trimmed);
        }

        return cleaned;
    }

    /// <summary>On-disk shape: <c>{ "links": [ ... ] }</c>.</summary>
    private sealed class LinksFile
    {
        public List<LinkEntry>? Links { get; set; }
    }
}

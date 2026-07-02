using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Lookup.Models;

namespace Lookup.Services;

/// <summary>A problem encountered while loading one data file (surfaced to the user).</summary>
public sealed record LoadError(string File, string Message);

/// <summary>Outcome of a load pass: the datasets that parsed, plus any per-file errors.</summary>
public sealed class LoadResult
{
    public List<LookupDataset> Datasets { get; } = new();
    public List<LoadError> Errors { get; } = new();
    public bool HasData => Datasets.Count > 0;
}

/// <summary>
/// Loads and validates every <c>*.json</c> file in the plugin's <c>data</c> directory.
/// A bad file is reported as a <see cref="LoadError"/> and skipped — it never throws out
/// of <see cref="Load"/>, so one broken file cannot take the whole plugin down.
/// </summary>
public static class DataLoader
{
    public static LoadResult Load(string dataDirectory)
    {
        var result = new LoadResult();

        if (string.IsNullOrWhiteSpace(dataDirectory) || !Directory.Exists(dataDirectory))
        {
            result.Errors.Add(new LoadError(dataDirectory ?? "(null)",
                "Data directory not found. Create a 'data' folder next to the plugin and add dataset JSON files."));
            return result;
        }

        var files = Directory.GetFiles(dataDirectory, "*.json", SearchOption.TopDirectoryOnly);
        Array.Sort(files, StringComparer.OrdinalIgnoreCase);
        if (files.Length == 0)
            result.Errors.Add(new LoadError(dataDirectory, "No .json files found in the data directory."));

        foreach (var file in files)
        {
            try
            {
                using var stream = File.OpenRead(file);
                var dataset = JsonSerializer.Deserialize<LookupDataset>(stream, JsonDefaults.SnakeCase);
                if (dataset is null)
                {
                    result.Errors.Add(new LoadError(file, "File is empty or not a JSON object."));
                    continue;
                }

                if (string.IsNullOrWhiteSpace(dataset.Dataset))
                    dataset.Dataset = Path.GetFileNameWithoutExtension(file);

                Sanitize(dataset);

                if (dataset.Items.Count == 0)
                {
                    result.Errors.Add(new LoadError(file, "Dataset contains no usable items."));
                    continue;
                }

                result.Datasets.Add(dataset);
            }
            catch (JsonException ex)
            {
                result.Errors.Add(new LoadError(file, "Invalid JSON: " + ex.Message));
            }
            catch (Exception ex)
            {
                result.Errors.Add(new LoadError(file, ex.Message));
            }
        }

        return result;
    }

    /// <summary>Guarantees non-null collections/strings and drops unusable items, so the
    /// rest of the pipeline never has to null-check an optional field.</summary>
    private static void Sanitize(LookupDataset ds)
    {
        ds.Items ??= new List<LookupItem>();
        ds.Items.RemoveAll(static i => i is null);

        foreach (var i in ds.Items)
        {
            // Strip null/empty array ELEMENTS too — a stray null would otherwise crash
            // SearchRecord's ToLowerInvariant projection and abort the whole index build.
            i.ParentCodes = Clean(i.ParentCodes);
            i.Keywords = Clean(i.Keywords);
            i.Aliases = Clean(i.Aliases);
            i.Code ??= "";
            i.Title ??= "";
            i.Description ??= "";
            i.Category ??= "";
            i.Url ??= "";
            i.Id ??= "";

            // code/id fallbacks so a record with only one of the two still works.
            if (string.IsNullOrEmpty(i.Code) && !string.IsNullOrEmpty(i.Id)) i.Code = i.Id;
            if (string.IsNullOrEmpty(i.Id) && !string.IsNullOrEmpty(i.Code)) i.Id = $"{ds.Dataset}:{i.Code}";
        }

        // A record with neither a code nor a title cannot be searched usefully.
        ds.Items.RemoveAll(static i => string.IsNullOrEmpty(i.Code) && string.IsNullOrEmpty(i.Title));
    }

    /// <summary>Returns a new list with null/blank entries removed and survivors trimmed.</summary>
    private static List<string> Clean(List<string>? values) =>
        values is null
            ? new List<string>()
            : values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim()).ToList();
}

using System;
using System.Collections.Generic;
using System.Linq;
using Lookup.Models;

namespace Lookup.Services;

/// <summary>Name, version and item count of a loaded dataset (for `lu datasets`).</summary>
public sealed record DatasetInfo(string Name, string Version, int Count);

/// <summary>A record that matched a query, with its score and title-highlight indices.</summary>
public sealed record ScoredRecord(SearchRecord Record, int Score, List<int> TitleHighlight);

/// <summary>
/// In-memory search index. Built once at startup (and on reload); queried on every
/// keystroke with a linear scan. A linear scan over a few thousand records is
/// sub-millisecond, which keeps the implementation simple and predictable; a dataset
/// of hundreds of thousands of rows would warrant an inverted index instead.
/// </summary>
public sealed class SearchIndex
{
    // Published by reference swap in Build(); read via local snapshot in Search().
    // This keeps Query (which runs on a Task) safe against a concurrent ReloadData,
    // which Flow dispatches on a separate thread — without locking the query hot path.
    private volatile IReadOnlyList<SearchRecord> _records = Array.Empty<SearchRecord>();
    private volatile IReadOnlyList<DatasetInfo> _datasets = Array.Empty<DatasetInfo>();

    public IReadOnlyList<DatasetInfo> Datasets => _datasets;
    public int Count => _records.Count;

    /// <summary>(Re)build the index from the given datasets, optionally filtering to a
    /// set of enabled dataset names (case-insensitive; null/empty means all). The new
    /// index is published atomically, so readers never observe a half-built state.</summary>
    public void Build(IEnumerable<LookupDataset> datasets, IReadOnlyCollection<string>? enabledDatasets = null)
    {
        var records = new List<SearchRecord>();
        var infos = new List<DatasetInfo>();

        foreach (var ds in datasets)
        {
            if (enabledDatasets is { Count: > 0 } &&
                !enabledDatasets.Contains(ds.Dataset, StringComparer.OrdinalIgnoreCase))
                continue;

            var n = 0;
            foreach (var item in ds.Items)
            {
                try
                {
                    records.Add(new SearchRecord(item, ds.Dataset));
                    n++;
                }
                catch
                {
                    // Skip a single malformed item rather than aborting the whole build.
                }
            }
            infos.Add(new DatasetInfo(ds.Dataset, ds.Version, n));
        }

        _records = records;
        _datasets = infos;
    }

    /// <summary>Score every record against the query and return the top
    /// <paramref name="maxResults"/> matches, highest score first.
    /// Pass <paramref name="datasetFilter"/> to restrict results to one dataset (case-insensitive).</summary>
    public List<ScoredRecord> Search(string query, int maxResults, string? datasetFilter = null)
    {
        var parsed = new ParsedQuery(query);
        if (parsed.Text.Length == 0) return new List<ScoredRecord>();

        var hits = new List<(SearchRecord Record, int Score)>();
        var records = _records; // stable snapshot for the duration of this scan
        foreach (var r in records)
        {
            if (datasetFilter is not null &&
                !string.Equals(r.Dataset, datasetFilter, StringComparison.OrdinalIgnoreCase))
                continue;

            var score = Scorer.Score(r, parsed);
            if (score > 0)
                hits.Add((r, score));
        }

        hits.Sort(static (a, b) =>
        {
            var c = b.Score.CompareTo(a.Score);
            if (c != 0) return c;
            // Deterministic tie-break: shorter code first, then alphabetical title.
            c = a.Record.CodeNorm.Length.CompareTo(b.Record.CodeNorm.Length);
            if (c != 0) return c;
            return string.CompareOrdinal(a.Record.TitleNorm, b.Record.TitleNorm);
        });

        if (maxResults > 0 && hits.Count > maxResults)
            hits.RemoveRange(maxResults, hits.Count - maxResults);

        // Highlights only for the visible page — computing them before the cut would
        // do the string scan for every matching record just to throw most away.
        var results = new List<ScoredRecord>(hits.Count);
        foreach (var (record, score) in hits)
            results.Add(new ScoredRecord(record, score, Scorer.ComputeHighlight(record, parsed)));
        return results;
    }
}

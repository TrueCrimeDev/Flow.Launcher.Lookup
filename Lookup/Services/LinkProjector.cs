using System;
using System.Collections.Generic;
using Lookup.Models;

namespace Lookup.Services;

/// <summary>A projected link dataset plus the map back to the entries that produced it.</summary>
public sealed class LinkProjection
{
    public LookupDataset Dataset { get; init; } = new();

    /// <summary>Item id → the link it came from. Main needs this to open a target,
    /// resolve an icon, or substitute a {q} parameter once a result is selected.</summary>
    public IReadOnlyDictionary<string, LinkEntry> ByItemId { get; init; } =
        new Dictionary<string, LinkEntry>();
}

/// <summary>
/// Turns user links into ordinary <see cref="LookupItem"/>s so they flow through the
/// existing <see cref="SearchIndex"/> and <see cref="Scorer"/> unchanged — one index,
/// one ranking path, links included.
/// </summary>
public static class LinkProjector
{
    /// <summary>Dataset name links are indexed under; also the scope for the `go` keyword.</summary>
    public const string DatasetName = "links";

    public static LinkProjection Project(IEnumerable<LinkEntry> links)
    {
        var dataset = new LookupDataset { Dataset = DatasetName, Version = "1" };
        var byItemId = new Dictionary<string, LinkEntry>(StringComparer.Ordinal);

        var ordinal = 0;
        foreach (var link in links)
        {
            if (link is null)
                continue;

            // Names are not required to be unique (aliases are), so the ordinal — not the
            // name — is what guarantees a stable, collision-free id.
            var id = $"{DatasetName}:{ordinal++}";

            var item = new LookupItem
            {
                Id = id,
                // The first alias is what the user types, so it plays the role a dataset
                // record's code plays: the short handle shown and matched against.
                Code = link.Aliases.Count > 0 ? link.Aliases[0] : "",
                Title = link.Name,
                Description = link.Description.Length > 0 ? link.Description : link.Target,
                Aliases = new List<string>(link.Aliases),
                Url = link.Target,
            };

            dataset.Items.Add(item);
            byItemId[id] = link;
        }

        return new LinkProjection { Dataset = dataset, ByItemId = byItemId };
    }
}

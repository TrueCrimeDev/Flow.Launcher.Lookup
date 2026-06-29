using System.Collections.Generic;

namespace Lookup.Models;

/// <summary>The on-disk shape of a single lookup JSON file.</summary>
public sealed class LookupDataset
{
    /// <summary>Short dataset name, e.g. "naics". Falls back to the file name if omitted.</summary>
    public string Dataset { get; set; } = "";

    /// <summary>Free-form version label, e.g. "2022".</summary>
    public string Version { get; set; } = "";

    public List<LookupItem> Items { get; set; } = new();
}

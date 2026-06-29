using System.Collections.Generic;

namespace Lookup.Models;

/// <summary>
/// One record in a lookup dataset.
///
/// The on-disk JSON uses snake_case; the mapping to these PascalCase properties is
/// handled centrally by the shared <see cref="System.Text.Json.JsonSerializerOptions"/>
/// in <see cref="Lookup.Services.DataLoader"/> (PropertyNamingPolicy = SnakeCaseLower),
/// so no per-property attributes are needed here.
///
/// Every field defaults to a non-null empty value, so a record that omits optional
/// fields never produces a null reference further down the pipeline.
/// </summary>
public sealed class LookupItem
{
    /// <summary>Globally unique id. Should be unique across all loaded datasets
    /// (e.g. "naics:541511"); the loader synthesises one from the code if missing.</summary>
    public string Id { get; set; } = "";

    /// <summary>Dataset-local identifier shown to the user and copied on Enter.</summary>
    public string Code { get; set; } = "";

    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string Category { get; set; } = "";
    public List<string> ParentCodes { get; set; } = new();
    public List<string> Keywords { get; set; } = new();
    public List<string> Aliases { get; set; } = new();
    public string Url { get; set; } = "";
}

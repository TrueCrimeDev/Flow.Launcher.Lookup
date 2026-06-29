using System;
using System.Collections.Generic;
using System.Linq;
using Lookup.Services;

namespace Lookup.Models;

/// <summary>
/// An indexed, search-optimised view of a <see cref="LookupItem"/>.
///
/// All lowercased / tokenised forms are computed once at index-build time so the
/// per-keystroke hot path allocates nothing beyond the parsed query itself.
/// </summary>
public sealed class SearchRecord
{
    public LookupItem Item { get; }
    public string Dataset { get; }

    public string CodeLower { get; }
    public string TitleLower { get; }
    public string DescriptionLower { get; }
    public string CategoryLower { get; }
    public string[] KeywordsLower { get; }
    public string[] AliasesLower { get; }
    public string[] ParentCodesLower { get; }

    /// <summary>Distinct whole words drawn from title, category, keywords and aliases.
    /// Used for whole-word matching and as the candidate pool for typo tolerance.</summary>
    public HashSet<string> Words { get; }

    public SearchRecord(LookupItem item, string dataset)
    {
        Item = item;
        Dataset = dataset;

        CodeLower = item.Code.ToLowerInvariant();
        TitleLower = item.Title.ToLowerInvariant();
        DescriptionLower = item.Description.ToLowerInvariant();
        CategoryLower = item.Category.ToLowerInvariant();
        // Guard against null/blank elements in case a record bypassed DataLoader.Sanitize.
        KeywordsLower = Lower(item.Keywords);
        AliasesLower = Lower(item.Aliases);
        ParentCodesLower = Lower(item.ParentCodes);

        Words = new HashSet<string>(StringComparer.Ordinal);
        AddWords(TitleLower);
        AddWords(CategoryLower);
        foreach (var k in KeywordsLower) AddWords(k);
        foreach (var a in AliasesLower) AddWords(a);
    }

    private void AddWords(string text)
    {
        foreach (var w in TextUtils.Tokenize(text)) Words.Add(w);
    }

    private static string[] Lower(IEnumerable<string>? values) =>
        values is null
            ? Array.Empty<string>()
            : values.Where(v => !string.IsNullOrEmpty(v)).Select(v => v.ToLowerInvariant()).ToArray();
}

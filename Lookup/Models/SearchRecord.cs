using System;
using System.Collections.Generic;
using System.Linq;
using Lookup.Services;

namespace Lookup.Models;

/// <summary>
/// An indexed, search-optimised view of a <see cref="LookupItem"/>.
///
/// All normalised / tokenised forms are computed once at index-build time so the
/// per-keystroke hot path allocates nothing beyond the parsed query itself.
///
/// Matching fields are fully normalised (lowercased, trimmed, single-spaced) with
/// <see cref="TextUtils.Normalize"/> — the same treatment every query gets — so
/// irregular whitespace in dataset JSON can never disable the exact/prefix/phrase
/// tiers. <see cref="TitleLower"/> alone stays un-collapsed: its character indices
/// must line up with the displayed title for highlighting.
/// </summary>
public sealed class SearchRecord
{
    public LookupItem Item { get; }
    public string Dataset { get; }

    public string CodeNorm { get; }
    /// <summary>Code with grouping separators removed, for code-like queries
    /// ("54-1511" and "541 511" both find 541511).</summary>
    public string CodeCompact { get; }
    public string TitleNorm { get; }
    /// <summary>Raw title lowercased (whitespace untouched) — used only for
    /// highlighting, where indices must map onto the displayed title.</summary>
    public string TitleLower { get; }
    public string DescriptionNorm { get; }
    public string CategoryNorm { get; }
    public string[] KeywordsNorm { get; }
    public string[] AliasesNorm { get; }
    /// <summary>Parent codes, normalised (prefix matching against word queries).</summary>
    public string[] ParentCodesNorm { get; }
    /// <summary>Parent codes, normalised and separator-stripped (prefix matching
    /// against code-like queries).</summary>
    public string[] ParentCodesCompact { get; }

    /// <summary>Distinct whole words drawn from title, category, keywords and aliases.
    /// Used for whole-word matching and as the candidate pool for typo tolerance.</summary>
    public HashSet<string> Words { get; }

    public SearchRecord(LookupItem item, string dataset)
    {
        Item = item;
        Dataset = dataset;

        CodeNorm = TextUtils.Normalize(item.Code);
        CodeCompact = TextUtils.CompactCode(CodeNorm);
        TitleNorm = TextUtils.Normalize(item.Title);
        TitleLower = item.Title.ToLowerInvariant();
        DescriptionNorm = TextUtils.Normalize(item.Description);
        CategoryNorm = TextUtils.Normalize(item.Category);
        KeywordsNorm = NormalizeAll(item.Keywords);
        AliasesNorm = NormalizeAll(item.Aliases);
        ParentCodesNorm = NormalizeAll(item.ParentCodes);
        ParentCodesCompact = ParentCodesNorm
            .Select(TextUtils.CompactCode)
            .Where(p => p.Length > 0)
            .ToArray();

        Words = new HashSet<string>(StringComparer.Ordinal);
        AddWords(TitleNorm);
        AddWords(CategoryNorm);
        foreach (var k in KeywordsNorm) AddWords(k);
        foreach (var a in AliasesNorm) AddWords(a);
    }

    private void AddWords(string text)
    {
        foreach (var w in TextUtils.Tokenize(text)) Words.Add(w);
    }

    /// <summary>Normalises every element; drops entries that are null/blank after
    /// normalisation (guards records that bypassed DataLoader.Sanitize).</summary>
    private static string[] NormalizeAll(IEnumerable<string>? values) =>
        values is null
            ? Array.Empty<string>()
            : values.Select(TextUtils.Normalize).Where(v => v.Length > 0).ToArray();
}

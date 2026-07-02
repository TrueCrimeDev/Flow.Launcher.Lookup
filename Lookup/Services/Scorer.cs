using System;
using System.Collections.Generic;
using Lookup.Models;

namespace Lookup.Services;

/// <summary>A user query parsed once per keystroke and reused across every record.</summary>
public sealed class ParsedQuery
{
    /// <summary>Normalised query: lowercased, trimmed, single-spaced.</summary>
    public string Text { get; }

    /// <summary>Alphanumeric word tokens of <see cref="Text"/>.</summary>
    public string[] Terms { get; }

    /// <summary>True when the query is purely a code (digits, optionally with separators),
    /// e.g. "541" or "541511". Such queries are matched against codes only, so numbers
    /// that happen to appear in prose descriptions never create noise.</summary>
    public bool LooksLikeCode { get; }

    /// <summary>For code-like queries: <see cref="Text"/> with the grouping separators
    /// stripped, so "54-1511" and "541 511" match the stored code "541511".</summary>
    public string CodeCompact { get; }

    public ParsedQuery(string raw)
    {
        Text = TextUtils.Normalize(raw);
        Terms = new List<string>(TextUtils.Tokenize(Text)).ToArray();
        LooksLikeCode = IsCodeLike(Text);
        CodeCompact = LooksLikeCode ? TextUtils.CompactCode(Text) : "";
    }

    private static bool IsCodeLike(string s)
    {
        if (s.Length == 0) return false;
        var anyDigit = false;
        foreach (var ch in s)
        {
            if (char.IsDigit(ch)) anyDigit = true;
            else if (ch is not ('-' or '.' or ' ')) return false; // digits + groupings only
        }
        return anyDigit;
    }
}

/// <summary>
/// Dependency-free relevance scorer.
///
/// Scores are arranged in fixed tiers (see the constants below). Tiers are spaced far
/// enough apart that a hit in a stronger field always outranks a hit in a weaker field:
/// every per-tier bonus is capped (coverage ≤200, all-words ≤200, fuzzy ≤500) below the
/// gap to the next tier. Equal final scores are ordered deterministically by <see cref="SearchIndex"/>.
///
/// Descending order (see constants): exact code &gt; code prefix &gt; exact title &gt;
/// exact alias &gt; title prefix &gt; exact keyword &gt; phrase (substring) &gt; all-words
/// &gt; category &gt; description &gt; parent-code &gt; typo. Keywords/aliases are tags, so
/// an exact keyword ranks just below a title prefix. A score of 0 means "not a match".
/// </summary>
public static class Scorer
{
    private const int ExactCode      = 10000;
    private const int CodePrefix     = 9000;
    private const int ExactTitle     = 8000;
    private const int ExactAlias     = 7400;
    private const int TitlePrefix    = 7000;
    private const int ExactKeyword   = 6400;
    private const int PhraseTitle    = 6000;  // full query is a substring of the title
    private const int CodeSubstring  = 5400;
    private const int PhraseAlias    = 5000;
    private const int PhraseKeyword  = 4400;
    private const int AllWords       = 4000;  // every query term appears as a whole word
    private const int PhraseCategory = 3200;
    private const int PhraseDesc     = 2600;
    private const int ParentCodeHit  = 1400;  // deliberately low: parent matches are broad/noisy
    private const int FuzzyBase      = 800;   // typo tolerance; + up to 500 bonus, stays < ParentCodeHit
    private const int FuzzyRange     = 500;

    /// <summary>Typo matches below this similarity are ignored as too weak.</summary>
    public const double FuzzyFloor = 0.72;

    /// <summary>Relevance score for a record against a query. 0 means it does not match.</summary>
    public static int Score(SearchRecord r, ParsedQuery q)
    {
        if (q.Text.Length == 0) return 0;
        var best = 0;

        // ---- Code matches (always attempted; codes may be numeric or alphanumeric) ----
        // Code-like queries compare separator-stripped forms so "54-1511" and "541 511"
        // still find 541511; word queries compare the normalised text verbatim.
        var codeQ = q.LooksLikeCode ? q.CodeCompact : q.Text;
        var code = q.LooksLikeCode ? r.CodeCompact : r.CodeNorm;
        if (code.Length > 0 && codeQ.Length > 0)
        {
            if (code == codeQ)
                best = Max(best, ExactCode + 200);
            else if (code.StartsWith(codeQ, StringComparison.Ordinal))
                best = Max(best, CodePrefix + CoverageBonus(codeQ.Length, code.Length));
            // Substring compares the un-stripped forms: on compacted codes, digits that
            // are only adjacent after stripping ("31-33" → "3133") would phantom-match
            // fragments like "13" that never appear in the displayed code.
            else if (r.CodeNorm.Contains(q.Text, StringComparison.Ordinal))
                best = Max(best, CodeSubstring);
        }

        // Parent codes: prefix only, ≥2 chars, low weight (so "54" doesn't flood results).
        // Same form on both sides: stripped vs stripped for code-like queries, normalised
        // vs normalised for word queries (a parent like "29 USC" must match typed "29 usc").
        var parents = q.LooksLikeCode ? r.ParentCodesCompact : r.ParentCodesNorm;
        if (codeQ.Length >= 2)
            foreach (var p in parents)
                if (p.StartsWith(codeQ, StringComparison.Ordinal))
                {
                    best = Max(best, ParentCodeHit);
                    break;
                }

        // Pure-numeric queries are code lookups only — stop before matching prose.
        if (q.LooksLikeCode) return best;

        // ---- Exact field equality ----
        if (r.TitleNorm == q.Text) best = Max(best, ExactTitle);
        best = Max(best, ExactInArray(r.AliasesNorm, q.Text, ExactAlias));
        best = Max(best, ExactInArray(r.KeywordsNorm, q.Text, ExactKeyword));

        // ---- Prefix / phrase (substring) matches ----
        if (r.TitleNorm.StartsWith(q.Text, StringComparison.Ordinal))
            best = Max(best, TitlePrefix + CoverageBonus(q.Text.Length, r.TitleNorm.Length));
        else if (r.TitleNorm.Contains(q.Text, StringComparison.Ordinal))
            best = Max(best, PhraseTitle);

        best = Max(best, PhraseInArray(r.AliasesNorm, q.Text, PhraseAlias));
        best = Max(best, PhraseInArray(r.KeywordsNorm, q.Text, PhraseKeyword));
        if (r.CategoryNorm.Contains(q.Text, StringComparison.Ordinal)) best = Max(best, PhraseCategory);
        if (r.DescriptionNorm.Contains(q.Text, StringComparison.Ordinal)) best = Max(best, PhraseDesc);

        // ---- All-words: every query term is a whole word somewhere in the record ----
        if (q.Terms.Length > 1 && AllTermsAreWords(r, q.Terms))
            best = Max(best, AllWords + Math.Min(q.Terms.Length, 10) * 20); // cap bonus at +200

        // ---- Typo tolerance: the fuzzy tier tops out at FuzzyBase + FuzzyRange (1300),
        // below every other non-zero tier, so it can only matter when nothing matched. ----
        if (best == 0)
        {
            var sim = FuzzySimilarity(r, q.Terms);
            if (sim >= FuzzyFloor)
                best = Max(best, FuzzyBase + (int)((sim - FuzzyFloor) / (1 - FuzzyFloor) * FuzzyRange));
        }

        return best;
    }

    /// <summary>Indices into the record's displayed title to highlight (where the query
    /// appears). Uses the cached raw-lowercased title so indices line up with what Flow
    /// renders. Returns an empty list when the query is not a visible substring.</summary>
    public static List<int> ComputeHighlight(SearchRecord r, ParsedQuery q)
    {
        var result = new List<int>();
        if (q.Text.Length == 0 || q.LooksLikeCode) return result;
        var idx = r.TitleLower.IndexOf(q.Text, StringComparison.Ordinal);
        if (idx < 0) return result;
        for (var i = idx; i < idx + q.Text.Length; i++) result.Add(i);
        return result;
    }

    // --- helpers -------------------------------------------------------------

    private static int Max(int a, int b) => a > b ? a : b;

    /// <summary>0..200 reward for a match that covers more of the target field.</summary>
    private static int CoverageBonus(int matchLen, int fieldLen) =>
        fieldLen <= 0 ? 0 : (int)(200.0 * matchLen / fieldLen);

    private static int ExactInArray(string[] values, string q, int tier)
    {
        foreach (var v in values)
            if (v == q) return tier;
        return 0;
    }

    private static int PhraseInArray(string[] values, string q, int tier)
    {
        foreach (var v in values)
            if (v.Contains(q, StringComparison.Ordinal)) return tier;
        return 0;
    }

    private static bool AllTermsAreWords(SearchRecord r, string[] terms)
    {
        foreach (var t in terms)
            if (!r.Words.Contains(t)) return false;
        return true;
    }

    /// <summary>Average best-word similarity across all query terms; 0 if any term has
    /// no sufficiently close word in the record.</summary>
    private static double FuzzySimilarity(SearchRecord r, string[] terms)
    {
        if (terms.Length == 0) return 0;
        double total = 0;
        foreach (var t in terms)
        {
            var termBest = 0.0;
            foreach (var w in r.Words)
            {
                // Length gap alone bounds similarity at 1 - |Δlen|/maxLen; skip the
                // O(n·m) Levenshtein pass when that bound is already below the floor.
                var max = Math.Max(t.Length, w.Length);
                if (max == 0 || 1.0 - (double)Math.Abs(t.Length - w.Length) / max < FuzzyFloor)
                    continue;

                var s = TextUtils.Similarity(t, w);
                if (s > termBest) termBest = s;
            }
            if (termBest < FuzzyFloor) return 0; // one weak term disqualifies the whole match
            total += termBest;
        }
        return total / terms.Length;
    }
}

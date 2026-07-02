using System;
using System.Collections.Generic;
using System.Text;

namespace Lookup.Services;

/// <summary>Small, allocation-light text helpers shared by the indexer and the scorer.</summary>
public static class TextUtils
{
    /// <summary>Lower-cases, trims, and collapses internal whitespace to single spaces.</summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var sb = new StringBuilder(text.Length);
        var lastSpace = false;
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (sb.Length > 0 && !lastSpace) { sb.Append(' '); lastSpace = true; }
            }
            else
            {
                sb.Append(char.ToLowerInvariant(ch));
                lastSpace = false;
            }
        }
        // Trim a possible trailing space produced by the loop.
        if (sb.Length > 0 && sb[^1] == ' ') sb.Length--;
        return sb.ToString();
    }

    /// <summary>Removes the code-grouping separators ('-', '.', ' ') so "54-1511",
    /// "541 511" and "541511" all compare equal when matched as codes.</summary>
    public static string CompactCode(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
            if (ch is not ('-' or '.' or ' ')) sb.Append(ch);
        return sb.ToString();
    }

    /// <summary>Splits text into lowercase alphanumeric word tokens (punctuation is a separator).</summary>
    public static IEnumerable<string> Tokenize(string? text)
    {
        if (string.IsNullOrEmpty(text)) yield break;
        var start = -1;
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsLetterOrDigit(text[i]))
            {
                if (start < 0) start = i;
            }
            else if (start >= 0)
            {
                yield return text.Substring(start, i - start).ToLowerInvariant();
                start = -1;
            }
        }
        if (start >= 0) yield return text[start..].ToLowerInvariant();
    }

    /// <summary>Levenshtein edit distance. Two-row variant: O(n*m) time, O(min(n,m)) space.</summary>
    public static int Levenshtein(string a, string b)
    {
        if (a == b) return 0;
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;

        // Keep the shorter string on the inner axis to minimise the row buffers.
        if (a.Length > b.Length) (a, b) = (b, a);

        var prev = new int[a.Length + 1];
        var curr = new int[a.Length + 1];
        for (var i = 0; i <= a.Length; i++) prev[i] = i;

        for (var j = 1; j <= b.Length; j++)
        {
            curr[0] = j;
            for (var i = 1; i <= a.Length; i++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                curr[i] = Math.Min(Math.Min(curr[i - 1] + 1, prev[i] + 1), prev[i - 1] + cost);
            }
            (prev, curr) = (curr, prev);
        }
        return prev[a.Length];
    }

    /// <summary>Normalised similarity in [0,1]: 1 == identical, 0 == nothing in common.</summary>
    public static double Similarity(string a, string b)
    {
        var max = Math.Max(a.Length, b.Length);
        if (max == 0) return 1.0;
        return 1.0 - (double)Levenshtein(a, b) / max;
    }
}

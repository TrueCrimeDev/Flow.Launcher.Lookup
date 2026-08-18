using System;
using System.Collections.Generic;
using System.Linq;
using Lookup.Models;

namespace Lookup.Services;

/// <summary>A link identified from the head of a query, plus the text left over for {q}.</summary>
public sealed record LinkMatch(LinkEntry Link, string Remainder);

/// <summary>
/// Resolves the leading words of a query to a link, so "jira ABC-123" can open the
/// Jira link with "ABC-123" substituted. Runs before the index: the index ranks
/// candidates, this decides what a parameterised link was actually asked to do.
/// </summary>
public static class QueryParser
{
    private const string Placeholder = "{q}";

    /// <summary>
    /// Finds the link whose name or alias occupies the longest run of leading whole
    /// words. Longest wins so that "gh pr 42" reaches the PR link rather than being
    /// swallowed by the shorter "gh"; whole-word matching stops "ghost" from doing so.
    /// </summary>
    public static LinkMatch? Match(string query, IEnumerable<LinkEntry> links)
    {
        var words = Tokenize(query);
        if (words.Length == 0)
            return null;

        LinkMatch? best = null;
        var bestWordCount = 0;

        foreach (var link in links)
        {
            if (link is null)
                continue;

            foreach (var key in Keys(link))
            {
                var keyWords = Tokenize(key);
                if (keyWords.Length == 0 || keyWords.Length > words.Length || keyWords.Length <= bestWordCount)
                    continue;

                if (!words.Take(keyWords.Length).SequenceEqual(keyWords, StringComparer.OrdinalIgnoreCase))
                    continue;

                bestWordCount = keyWords.Length;
                best = new LinkMatch(link, string.Join(' ', words.Skip(keyWords.Length)));
            }
        }

        return best;
    }

    /// <summary>
    /// Resolves a link's target for opening: substitutes <c>{q}</c> when present,
    /// URL-encoding the value only for web targets — a local path must keep its
    /// spaces and backslashes intact.
    /// </summary>
    public static string BuildTarget(LinkEntry link, string remainder)
    {
        if (!link.HasQueryPlaceholder)
            return link.Target;

        var value = IsWebTarget(link.Target) ? Uri.EscapeDataString(remainder ?? "") : remainder ?? "";
        return Replace(link.Target, Placeholder, value);
    }

    /// <summary>True when the link expects a parameter the user has not typed yet.
    /// Such a result is shown as a prompt rather than opened.</summary>
    public static bool NeedsParameter(LinkEntry link, string remainder) =>
        link.HasQueryPlaceholder && string.IsNullOrWhiteSpace(remainder);

    private static IEnumerable<string> Keys(LinkEntry link)
    {
        if (!string.IsNullOrWhiteSpace(link.Name))
            yield return link.Name;

        foreach (var alias in link.Aliases)
            if (!string.IsNullOrWhiteSpace(alias))
                yield return alias;
    }

    private static string[] Tokenize(string? text) =>
        (text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    private static bool IsWebTarget(string target) =>
        target.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
        target.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    /// <summary>Case-insensitive replace-all (string.Replace's comparison overload
    /// exists, but spelling it out keeps the intent obvious at the call site).</summary>
    private static string Replace(string haystack, string needle, string value) =>
        haystack.Replace(needle, value, StringComparison.OrdinalIgnoreCase);
}

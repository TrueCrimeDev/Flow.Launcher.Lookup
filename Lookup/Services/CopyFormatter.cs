using Lookup.Models;

namespace Lookup.Services;

/// <summary>
/// Pure text formatting for the clipboard copy variants offered on a result
/// (Enter's single-field copy plus the combined context-menu formats). No SDK
/// dependency, so it lives in the test assembly alongside the rest of the core.
/// </summary>
public static class CopyFormatter
{
    /// <summary>"{code} - {title}", or just whichever of the two is present.</summary>
    public static string CodeTitle(LookupItem item) =>
        string.IsNullOrEmpty(item.Code) || string.IsNullOrEmpty(item.Title)
            ? (string.IsNullOrEmpty(item.Code) ? item.Title : item.Code)
            : $"{item.Code} - {item.Title}";

    /// <summary>Code/title header followed by a blank line and the full NAICS
    /// description. Falls back to <see cref="CodeTitle"/> when there is no
    /// description to append.</summary>
    public static string FullDetails(LookupItem item)
    {
        var header = CodeTitle(item);
        return string.IsNullOrWhiteSpace(item.Description)
            ? header
            : $"{header}\n\n{item.Description}";
    }
}

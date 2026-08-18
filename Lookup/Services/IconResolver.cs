using System;
using System.IO;
using Lookup.Models;

namespace Lookup.Services;

/// <summary>How a link's icon should be rendered.</summary>
public enum IconKind
{
    /// <summary>Value is a path Flow can load — an image file, or a target whose
    /// icon Flow extracts (file, folder or executable).</summary>
    Image,

    /// <summary>Value is a glyph character from <see cref="IconResolver.FontFamily"/>.</summary>
    Glyph,
}

/// <summary>
/// The icon to render, plus an optional favicon fetch the caller should start in the
/// background. A spec never performs I/O itself.
/// </summary>
public sealed record IconSpec(IconKind Kind, string Value, bool Rounded = false, string? FetchUrl = null);

/// <summary>
/// Decides which of Flow's icon channels a link should use. Pure: the two facts that
/// depend on the outside world — whether favicons are enabled and whether one is
/// already cached — are supplied by the caller, so the whole decision table is testable.
/// </summary>
public static class IconResolver
{
    public const string FontFamily = "Segoe Fluent Icons";

    /// <summary>Segoe Fluent "Link" glyph; the fallback for anything not resolvable to an image.</summary>
    public const string LinkGlyph = "";

    private const string GlyphPrefix = "glyph:";
    private const string Placeholder = "{q}";

    public static IconSpec Resolve(LinkEntry link, bool faviconsEnabled, string? cachedFaviconPath)
    {
        var icon = (link.Icon ?? "").Trim();

        if (icon.StartsWith(GlyphPrefix, StringComparison.OrdinalIgnoreCase))
            return new IconSpec(IconKind.Glyph, icon[GlyphPrefix.Length..]);

        if (icon.Length > 0 && !icon.Equals("auto", StringComparison.OrdinalIgnoreCase))
            return new IconSpec(IconKind.Image, icon);

        // A parameterised target is not a real path or URL until it is filled in;
        // strip the placeholder so the icon reflects the site or folder behind it.
        var target = (link.Target ?? "").Replace(Placeholder, "", StringComparison.OrdinalIgnoreCase).Trim();

        if (target.Length == 0)
            return new IconSpec(IconKind.Glyph, LinkGlyph);

        if (IsWebTarget(target))
        {
            if (!faviconsEnabled)
                return new IconSpec(IconKind.Glyph, LinkGlyph);

            return string.IsNullOrEmpty(cachedFaviconPath)
                ? new IconSpec(IconKind.Glyph, LinkGlyph, Rounded: false, FetchUrl: target)
                // Site favicons are square; rounding them keeps the result row aligned
                // with Flow's other icons.
                : new IconSpec(IconKind.Image, cachedFaviconPath, Rounded: true);
        }

        return IsLocalTarget(target)
            ? new IconSpec(IconKind.Image, target)
            : new IconSpec(IconKind.Glyph, LinkGlyph);
    }

    private static bool IsWebTarget(string target) =>
        target.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
        target.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    /// <summary>Decided by the shape of the string, not by touching the filesystem —
    /// a target that has gone missing still deserves a row, just with a fallback icon.</summary>
    private static bool IsLocalTarget(string target)
    {
        if (target.StartsWith(@"\\", StringComparison.Ordinal) || target.StartsWith("%", StringComparison.Ordinal))
            return true;

        try
        {
            return Path.IsPathRooted(target);
        }
        catch (ArgumentException)
        {
            return false; // invalid path characters — not a usable local target
        }
    }
}

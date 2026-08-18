using System;
using System.Collections.Generic;

namespace Lookup.Models;

/// <summary>
/// One user-defined link: a named target that Enter opens.
///
/// The target may be a URL, file, folder or executable, and may contain a <c>{q}</c>
/// placeholder — the text typed after the alias is substituted into it, which is what
/// lets a link double as a site query.
///
/// On-disk keys are snake_case, mapped by <see cref="Lookup.Services.JsonDefaults"/>.
/// </summary>
public sealed class LinkEntry
{
    /// <summary>Display title, and the primary match key.</summary>
    public string Name { get; set; } = "";

    /// <summary>Extra match keys. Matched case-insensitively; must be unique across all links.</summary>
    public List<string> Aliases { get; set; } = new();

    /// <summary>URL, file path, folder path or executable. May contain <c>{q}</c>.</summary>
    public string Target { get; set; } = "";

    /// <summary><c>auto</c> (default), <c>glyph:&lt;char&gt;</c>, or a path to an image.</summary>
    public string Icon { get; set; } = "auto";

    /// <summary>Subtitle text. Falls back to the target when blank.</summary>
    public string Description { get; set; } = "";

    /// <summary>True when the target expects a parameter typed after the alias.</summary>
    public bool HasQueryPlaceholder =>
        Target.Contains("{q}", StringComparison.OrdinalIgnoreCase);
}

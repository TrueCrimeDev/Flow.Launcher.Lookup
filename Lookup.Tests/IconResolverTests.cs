using Lookup.Models;
using Lookup.Services;
using Xunit;

namespace Lookup.Tests;

/// <summary>
/// The icon decision table. Kept a pure function of the entry plus two facts the
/// caller supplies (favicons on? already cached?) so every branch is testable
/// without touching disk or network.
/// </summary>
public sealed class IconResolverTests
{
    private static LinkEntry Link(string target, string icon = "auto") =>
        new() { Name = "X", Target = target, Icon = icon };

    [Fact]
    public void GlyphOverride_WinsOverEverything()
    {
        var spec = IconResolver.Resolve(Link("https://github.com", "glyph:\uE8A5"), faviconsEnabled: true, cachedFaviconPath: @"C:\cache\github.png");

        Assert.Equal(IconKind.Glyph, spec.Kind);
        Assert.Equal("\uE8A5", spec.Value);
        Assert.Null(spec.FetchUrl);
    }

    [Fact]
    public void ImagePathOverride_IsUsedAsIs()
    {
        var spec = IconResolver.Resolve(Link("https://github.com", @"C:\icons\gh.png"), true, null);

        Assert.Equal(IconKind.Image, spec.Kind);
        Assert.Equal(@"C:\icons\gh.png", spec.Value);
    }

    [Fact]
    public void AutoIcon_FallsThroughToDetection()
    {
        var spec = IconResolver.Resolve(Link(@"C:\Program Files\App\app.exe", "auto"), true, null);

        Assert.Equal(IconKind.Image, spec.Kind);
        Assert.Equal(@"C:\Program Files\App\app.exe", spec.Value);
    }

    [Fact]
    public void LocalPath_ResolvesToTheTargetItself()
    {
        // Flow's ImageLoader extracts the real icon from a file, folder or exe path,
        // so the plugin never needs extraction code of its own.
        var spec = IconResolver.Resolve(Link(@"C:\Users\me\Notes"), true, null);

        Assert.Equal(IconKind.Image, spec.Kind);
        Assert.Equal(@"C:\Users\me\Notes", spec.Value);
        Assert.False(spec.Rounded);
    }

    [Fact]
    public void UncPath_IsTreatedAsALocalTarget()
    {
        var spec = IconResolver.Resolve(Link(@"\\server\share\tool.exe"), true, null);

        Assert.Equal(IconKind.Image, spec.Kind);
    }

    [Fact]
    public void LocalPathWithPlaceholder_DropsThePlaceholderBeforeResolving()
    {
        var spec = IconResolver.Resolve(Link(@"C:\src\{q}"), true, null);

        Assert.Equal(IconKind.Image, spec.Kind);
        Assert.Equal(@"C:\src\", spec.Value);
    }

    [Fact]
    public void WebTarget_WithCachedFavicon_UsesItRounded()
    {
        var spec = IconResolver.Resolve(Link("https://github.com"), true, @"C:\cache\github.com.png");

        Assert.Equal(IconKind.Image, spec.Kind);
        Assert.Equal(@"C:\cache\github.com.png", spec.Value);
        Assert.True(spec.Rounded);
        Assert.Null(spec.FetchUrl);
    }

    [Fact]
    public void WebTarget_WithoutCache_ShowsGlyphAndAsksForAFetch()
    {
        var spec = IconResolver.Resolve(Link("https://github.com"), true, null);

        Assert.Equal(IconKind.Glyph, spec.Kind);
        Assert.Equal(IconResolver.LinkGlyph, spec.Value);
        Assert.Equal("https://github.com", spec.FetchUrl);
    }

    [Fact]
    public void WebTarget_WithPlaceholder_RequestsTheFetchWithoutIt()
    {
        var spec = IconResolver.Resolve(Link("https://acme.atlassian.net/browse/{q}"), true, null);

        Assert.Equal("https://acme.atlassian.net/browse/", spec.FetchUrl);
    }

    [Fact]
    public void WebTarget_WithFaviconsDisabled_NeverAsksForAFetch()
    {
        var spec = IconResolver.Resolve(Link("https://github.com"), faviconsEnabled: false, cachedFaviconPath: null);

        Assert.Equal(IconKind.Glyph, spec.Kind);
        Assert.Null(spec.FetchUrl);
    }

    [Fact]
    public void WebTarget_WithFaviconsDisabled_IgnoresAStaleCacheEntry()
    {
        var spec = IconResolver.Resolve(Link("https://github.com"), false, @"C:\cache\github.com.png");

        Assert.Equal(IconKind.Glyph, spec.Kind);
    }

    [Fact]
    public void UnknownScheme_FallsBackToTheLinkGlyph()
    {
        var spec = IconResolver.Resolve(Link("mailto:someone@example.com"), true, null);

        Assert.Equal(IconKind.Glyph, spec.Kind);
        Assert.Equal(IconResolver.LinkGlyph, spec.Value);
        Assert.Null(spec.FetchUrl);
    }

    [Fact]
    public void EmptyTarget_FallsBackToTheLinkGlyph()
    {
        Assert.Equal(IconKind.Glyph, IconResolver.Resolve(Link(""), true, null).Kind);
    }
}

using System.Collections.Generic;
using System.Linq;
using Lookup.Models;
using Lookup.Services;
using Xunit;

namespace Lookup.Tests;

/// <summary>
/// The parse step that turns "jira ABC-123" into (the Jira link, "ABC-123").
/// This is what lets one entry both open a site and query it.
/// </summary>
public sealed class QueryParserTests
{
    private static LinkEntry Link(string name, string target, params string[] aliases) =>
        new() { Name = name, Target = target, Aliases = aliases.ToList() };

    private static List<LinkEntry> Links() => new()
    {
        Link("GitHub", "https://github.com", "gh"),
        Link("GitHub PR", "https://github.com/pulls/{q}", "gh pr"),
        Link("Jira issue", "https://acme.atlassian.net/browse/{q}", "jira"),
        Link("Notes folder", @"C:\Users\me\Notes"),
    };

    [Fact]
    public void Match_ExactAlias_ReturnsLinkWithEmptyRemainder()
    {
        var match = QueryParser.Match("gh", Links());

        Assert.NotNull(match);
        Assert.Equal("GitHub", match!.Link.Name);
        Assert.Equal("", match.Remainder);
    }

    [Fact]
    public void Match_AliasWithRemainder_SplitsOnTheAlias()
    {
        var match = QueryParser.Match("jira ABC-123", Links());

        Assert.Equal("Jira issue", match!.Link.Name);
        Assert.Equal("ABC-123", match.Remainder);
    }

    [Fact]
    public void Match_LongestAliasWins()
    {
        var match = QueryParser.Match("gh pr 42", Links());

        Assert.Equal("GitHub PR", match!.Link.Name);
        Assert.Equal("42", match.Remainder);
    }

    [Fact]
    public void Match_NameMatchesToo()
    {
        var match = QueryParser.Match("Notes folder", Links());

        Assert.Equal("Notes folder", match!.Link.Name);
        Assert.Equal("", match.Remainder);
    }

    [Fact]
    public void Match_IsCaseInsensitive()
    {
        Assert.Equal("Jira issue", QueryParser.Match("JIRA abc", Links())!.Link.Name);
    }

    [Fact]
    public void Match_RequiresAWholeToken()
    {
        // "ghost" starts with "gh" but is a different word; matching it would hijack
        // every query that happens to share a prefix with a short alias.
        Assert.Null(QueryParser.Match("ghost writer", Links()));
    }

    [Fact]
    public void Match_UnknownQuery_ReturnsNull()
    {
        Assert.Null(QueryParser.Match("something else", Links()));
    }

    [Fact]
    public void Match_EmptyQuery_ReturnsNull()
    {
        Assert.Null(QueryParser.Match("   ", Links()));
    }

    [Fact]
    public void Match_CollapsesExtraWhitespace()
    {
        var match = QueryParser.Match("  jira   ABC-123  ", Links());

        Assert.Equal("ABC-123", match!.Remainder);
    }

    [Fact]
    public void BuildTarget_WithoutPlaceholder_IgnoresRemainder()
    {
        var link = Link("GitHub", "https://github.com", "gh");

        Assert.Equal("https://github.com", QueryParser.BuildTarget(link, "anything"));
    }

    [Fact]
    public void BuildTarget_SubstitutesTheRemainder()
    {
        var link = Link("Jira", "https://acme.atlassian.net/browse/{q}", "jira");

        Assert.Equal("https://acme.atlassian.net/browse/ABC-123", QueryParser.BuildTarget(link, "ABC-123"));
    }

    [Fact]
    public void BuildTarget_UrlEncodesForWebTargets()
    {
        var link = Link("Search", "https://example.com/search?q={q}", "s");

        Assert.Equal("https://example.com/search?q=hello%20world", QueryParser.BuildTarget(link, "hello world"));
    }

    [Fact]
    public void BuildTarget_DoesNotUrlEncodeLocalPaths()
    {
        var link = Link("Project", @"C:\src\{q}", "p");

        Assert.Equal(@"C:\src\my project", QueryParser.BuildTarget(link, "my project"));
    }

    [Fact]
    public void BuildTarget_PlaceholderIsCaseInsensitiveAndRepeatable()
    {
        var link = Link("Dup", "https://x/{Q}/{q}", "d");

        Assert.Equal("https://x/a/a", QueryParser.BuildTarget(link, "a"));
    }

    [Fact]
    public void NeedsParameter_TrueOnlyWhenPlaceholderHasNothingToFill()
    {
        var parameterized = Link("Jira", "https://acme/browse/{q}", "jira");
        var plain = Link("GitHub", "https://github.com", "gh");

        Assert.True(QueryParser.NeedsParameter(parameterized, ""));
        Assert.False(QueryParser.NeedsParameter(parameterized, "ABC-1"));
        Assert.False(QueryParser.NeedsParameter(plain, ""));
    }
}

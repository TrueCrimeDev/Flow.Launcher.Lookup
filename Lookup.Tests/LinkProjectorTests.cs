using System.Linq;
using Lookup.Models;
using Lookup.Services;
using Xunit;

namespace Lookup.Tests;

/// <summary>
/// Links reach the user through the same index as every dataset, so the projection
/// into LookupItem is where link semantics have to survive intact.
/// </summary>
public sealed class LinkProjectorTests
{
    private static LinkEntry Link(string name, string target, params string[] aliases) =>
        new() { Name = name, Target = target, Aliases = aliases.ToList() };

    [Fact]
    public void Project_EmptyInput_ProducesEmptyDataset()
    {
        var projection = LinkProjector.Project(new LinkEntry[0]);

        Assert.Equal(LinkProjector.DatasetName, projection.Dataset.Dataset);
        Assert.Empty(projection.Dataset.Items);
        Assert.Empty(projection.ByItemId);
    }

    [Fact]
    public void Project_MapsNameToTitleAndTargetToUrl()
    {
        var projection = LinkProjector.Project(new[] { Link("GitHub", "https://github.com", "gh") });

        var item = Assert.Single(projection.Dataset.Items);
        Assert.Equal("GitHub", item.Title);
        Assert.Equal("https://github.com", item.Url);
    }

    [Fact]
    public void Project_FirstAliasBecomesCode()
    {
        var projection = LinkProjector.Project(new[] { Link("GitHub", "https://github.com", "gh", "hub") });

        var item = Assert.Single(projection.Dataset.Items);
        Assert.Equal("gh", item.Code);
        Assert.Equal(new[] { "gh", "hub" }, item.Aliases);
    }

    [Fact]
    public void Project_WithoutAliases_LeavesCodeEmpty()
    {
        var projection = LinkProjector.Project(new[] { Link("GitHub", "https://github.com") });

        Assert.Equal("", Assert.Single(projection.Dataset.Items).Code);
    }

    [Fact]
    public void Project_BlankDescription_FallsBackToTarget()
    {
        var projection = LinkProjector.Project(new[] { Link("GitHub", "https://github.com") });

        Assert.Equal("https://github.com", Assert.Single(projection.Dataset.Items).Description);
    }

    [Fact]
    public void Project_ExplicitDescription_IsKept()
    {
        var entry = Link("GitHub", "https://github.com");
        entry.Description = "Code host";

        var projection = LinkProjector.Project(new[] { entry });

        Assert.Equal("Code host", Assert.Single(projection.Dataset.Items).Description);
    }

    [Fact]
    public void Project_DuplicateNames_StillGetUniqueIds()
    {
        var projection = LinkProjector.Project(new[]
        {
            Link("GitHub", "https://github.com"),
            Link("GitHub", "https://gitea.local"),
        });

        var ids = projection.Dataset.Items.Select(i => i.Id).ToArray();
        Assert.Equal(2, ids.Distinct().Count());
        Assert.Equal(2, projection.ByItemId.Count);
    }

    [Fact]
    public void Project_ByItemId_ResolvesBackToTheOriginalEntry()
    {
        var entry = Link("Jira", "https://acme.atlassian.net/browse/{q}", "jira");

        var projection = LinkProjector.Project(new[] { entry });

        var item = Assert.Single(projection.Dataset.Items);
        Assert.Same(entry, projection.ByItemId[item.Id]);
    }

    [Fact]
    public void Project_IdsAreNamespacedToTheLinksDataset()
    {
        var projection = LinkProjector.Project(new[] { Link("GitHub", "https://github.com") });

        Assert.StartsWith(LinkProjector.DatasetName + ":", Assert.Single(projection.Dataset.Items).Id);
    }

    [Fact]
    public void ProjectedLinks_AreSearchableThroughTheNormalIndex()
    {
        var projection = LinkProjector.Project(new[]
        {
            Link("GitHub", "https://github.com", "gh"),
            Link("Jira issue", "https://acme.atlassian.net/browse/{q}", "jira"),
        });

        var index = new SearchIndex();
        index.Build(new[] { projection.Dataset });

        var hits = index.Search("github", 10);

        Assert.Equal("GitHub", hits[0].Record.Item.Title);
        Assert.Equal(LinkProjector.DatasetName, hits[0].Record.Dataset);
    }

    [Fact]
    public void ProjectedLinks_AreReachableByAlias()
    {
        var projection = LinkProjector.Project(new[] { Link("Jira issue", "https://acme/{q}", "jira") });

        var index = new SearchIndex();
        index.Build(new[] { projection.Dataset });

        Assert.NotEmpty(index.Search("jira", 10));
    }
}

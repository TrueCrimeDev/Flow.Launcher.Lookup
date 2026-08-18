using System.Collections.Generic;
using System.Linq;
using Lookup.Models;
using Lookup.Services;
using Xunit;

namespace Lookup.Tests;

/// <summary>
/// A two-letter link alias competes against thousands of dataset rows, so links need a
/// deterministic pin. These tests also guard the other half of the bargain: dataset-only
/// ranking must be exactly what it was before links existed.
/// </summary>
public sealed class LinkRankingTests
{
    private static LookupDataset Dataset(params (string Code, string Title)[] items) => new()
    {
        Dataset = "naics",
        Version = "2022",
        Items = items.Select(i => new LookupItem
        {
            Id = "naics:" + i.Code,
            Code = i.Code,
            Title = i.Title,
        }).ToList(),
    };

    private static LinkEntry Link(string name, string target, params string[] aliases) =>
        new() { Name = name, Target = target, Aliases = aliases.ToList() };

    private static SearchIndex IndexWith(IEnumerable<LinkEntry> links, LookupDataset dataset)
    {
        var index = new SearchIndex();
        index.Build(new[] { dataset, LinkProjector.Project(links).Dataset });
        return index;
    }

    [Fact]
    public void ExactAlias_OutranksAFuzzyDatasetHit()
    {
        var index = IndexWith(
            new[] { Link("GitHub", "https://github.com", "gh") },
            Dataset(("541511", "GH Holdings and Ghost Writing Services")));

        var hits = index.Search("gh", 10);

        Assert.Equal("GitHub", hits[0].Record.Item.Title);
        Assert.Equal(LinkProjector.DatasetName, hits[0].Record.Dataset);
    }

    [Fact]
    public void ExactLinkName_OutranksADatasetTitleWithTheSameWords()
    {
        var index = IndexWith(
            new[] { Link("payroll", "https://payroll.example.com") },
            Dataset(("541214", "payroll")));

        Assert.Equal(LinkProjector.DatasetName, index.Search("payroll", 10)[0].Record.Dataset);
    }

    [Fact]
    public void AliasPrefix_OutranksADatasetPrefixMatch()
    {
        var index = IndexWith(
            new[] { Link("Jira issue", "https://acme/browse/{q}", "jira") },
            Dataset(("541511", "Jiranium Refining")));

        Assert.Equal("Jira issue", index.Search("jir", 10)[0].Record.Item.Title);
    }

    [Fact]
    public void LinkMatchingOnlyByDescription_IsNotPinned()
    {
        // The pin is for names and aliases. A stray word in a link's description must
        // not vault it over a dataset row that matches by title.
        var link = Link("Ops runbook", "https://ops.example.com");
        link.Description = "construction site checklists";

        var index = IndexWith(new[] { link }, Dataset(("236220", "Construction")));

        Assert.Equal("naics", index.Search("construction", 10)[0].Record.Dataset);
    }

    [Fact]
    public void DatasetOnlyRanking_IsUnchangedByThePin()
    {
        var dataset = Dataset(
            ("236220", "Commercial Construction"),
            ("238100", "Construction"),
            ("999999", "Building and Construction Supplies"));

        var withoutLinks = new SearchIndex();
        withoutLinks.Build(new[] { dataset });
        var before = withoutLinks.Search("construction", 10)
            .Select(h => (h.Record.Item.Title, h.Score)).ToArray();

        var withLinks = IndexWith(new[] { Link("GitHub", "https://github.com", "gh") }, dataset);
        var after = withLinks.Search("construction", 10)
            .Where(h => h.Record.Dataset == "naics")
            .Select(h => (h.Record.Item.Title, h.Score)).ToArray();

        // Same rows, same scores, same order — adding links changes nothing for datasets.
        Assert.Equal(before, after);
        Assert.Equal("Construction", before[0].Title); // exact title still leads
    }

    [Fact]
    public void NonLinkRecords_NeverReceiveThePin()
    {
        var dataset = Dataset(("541511", "GitHub"));
        var index = new SearchIndex();
        index.Build(new[] { dataset });

        var datasetOnly = index.Search("github", 10)[0].Score;

        var withLink = IndexWith(new[] { Link("GitHub", "https://github.com", "gh") }, dataset);
        var hits = withLink.Search("github", 10);

        Assert.Equal(LinkProjector.DatasetName, hits[0].Record.Dataset);
        Assert.Equal(datasetOnly, hits[1].Score); // the dataset row scores exactly as before
    }

    [Fact]
    public void ScopingToTheLinksDataset_ReturnsOnlyLinks()
    {
        var index = IndexWith(
            new[] { Link("GitHub", "https://github.com", "gh") },
            Dataset(("541511", "GitHub Enterprise Consulting")));

        var hits = index.Search("github", 10, LinkProjector.DatasetName);

        Assert.All(hits, h => Assert.Equal(LinkProjector.DatasetName, h.Record.Dataset));
        Assert.Single(hits);
    }
}

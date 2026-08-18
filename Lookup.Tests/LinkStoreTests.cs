using System.IO;
using System.Linq;
using Lookup.Models;
using Lookup.Services;
using Xunit;

namespace Lookup.Tests;

/// <summary>
/// Behaviour of the user-editable links.json store. The guiding rule, inherited from
/// PluginConfig: a broken file degrades, it never throws.
/// </summary>
public sealed class LinkStoreTests
{
    private static string NewSettingsDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "lookup-linkstore-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void WriteLinks(string dir, string json) =>
        File.WriteAllText(LinkStore.FilePath(dir), json);

    [Fact]
    public void Load_MissingFile_ReturnsEmptyWithoutError()
    {
        var result = LinkStore.Load(NewSettingsDir());

        Assert.Empty(result.Links);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Load_MissingDirectory_ReturnsEmptyWithoutError()
    {
        var result = LinkStore.Load(Path.Combine(Path.GetTempPath(), "lookup-does-not-exist-" + Path.GetRandomFileName()));

        Assert.Empty(result.Links);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Load_ValidFile_ReturnsEntries()
    {
        var dir = NewSettingsDir();
        WriteLinks(dir, """
        {
          "links": [
            { "name": "GitHub", "aliases": ["gh"], "target": "https://github.com" },
            { "name": "Jira issue", "aliases": ["jira"], "target": "https://acme.atlassian.net/browse/{q}" }
          ]
        }
        """);

        var result = LinkStore.Load(dir);

        Assert.Empty(result.Errors);
        Assert.Equal(2, result.Links.Count);
        Assert.Equal("GitHub", result.Links[0].Name);
        Assert.Equal("gh", Assert.Single(result.Links[0].Aliases));
        Assert.Equal("https://acme.atlassian.net/browse/{q}", result.Links[1].Target);
    }

    [Fact]
    public void Load_MalformedJson_ReportsErrorAndReturnsNoLinks()
    {
        var dir = NewSettingsDir();
        WriteLinks(dir, "{ this is not json");

        var result = LinkStore.Load(dir);

        Assert.Empty(result.Links);
        var error = Assert.Single(result.Errors);
        Assert.Contains("JSON", error.Message);
    }

    [Fact]
    public void Load_EntryMissingTarget_IsSkippedAndOthersSurvive()
    {
        var dir = NewSettingsDir();
        WriteLinks(dir, """
        {
          "links": [
            { "name": "Broken" },
            { "name": "GitHub", "target": "https://github.com" }
          ]
        }
        """);

        var result = LinkStore.Load(dir);

        Assert.Equal("GitHub", Assert.Single(result.Links).Name);
        Assert.Contains(result.Errors, e => e.Name == "Broken");
    }

    [Fact]
    public void Load_EntryMissingName_IsSkipped()
    {
        var dir = NewSettingsDir();
        WriteLinks(dir, """{ "links": [ { "target": "https://github.com" } ] }""");

        var result = LinkStore.Load(dir);

        Assert.Empty(result.Links);
        Assert.Single(result.Errors);
    }

    [Fact]
    public void Load_DuplicateAlias_KeepsFirstAndReportsError()
    {
        var dir = NewSettingsDir();
        WriteLinks(dir, """
        {
          "links": [
            { "name": "GitHub", "aliases": ["gh"], "target": "https://github.com" },
            { "name": "Gitea",  "aliases": ["GH"], "target": "https://gitea.local" }
          ]
        }
        """);

        var result = LinkStore.Load(dir);

        Assert.Equal(2, result.Links.Count);
        Assert.Equal("gh", Assert.Single(result.Links[0].Aliases));
        Assert.Empty(result.Links[1].Aliases);
        Assert.Contains(result.Errors, e => e.Message.Contains("gh", System.StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Load_BlankAndNullAliases_AreDropped()
    {
        var dir = NewSettingsDir();
        WriteLinks(dir, """
        { "links": [ { "name": "GitHub", "aliases": ["gh", "  ", null, " hub "], "target": "https://github.com" } ] }
        """);

        var link = Assert.Single(LinkStore.Load(dir).Links);

        Assert.Equal(new[] { "gh", "hub" }, link.Aliases);
    }

    [Fact]
    public void Load_MissingIcon_DefaultsToAuto()
    {
        var dir = NewSettingsDir();
        WriteLinks(dir, """{ "links": [ { "name": "GitHub", "target": "https://github.com" } ] }""");

        Assert.Equal("auto", Assert.Single(LinkStore.Load(dir).Links).Icon);
    }

    [Fact]
    public void Save_ThenLoad_RoundTrips()
    {
        var dir = NewSettingsDir();
        var links = new[]
        {
            new LinkEntry { Name = "GitHub", Aliases = { "gh" }, Target = "https://github.com" },
            new LinkEntry { Name = "Jira", Aliases = { "jira" }, Target = "https://acme.atlassian.net/browse/{q}", Icon = "glyph:", Description = "Open a ticket" },
        };

        LinkStore.Save(dir, links);
        var result = LinkStore.Load(dir);

        Assert.Empty(result.Errors);
        Assert.Equal(2, result.Links.Count);
        Assert.Equal("Jira", result.Links[1].Name);
        Assert.Equal("glyph:", result.Links[1].Icon);
        Assert.Equal("Open a ticket", result.Links[1].Description);
    }

    [Fact]
    public void Save_CreatesMissingDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "lookup-save-" + Path.GetRandomFileName());

        LinkStore.Save(dir, new[] { new LinkEntry { Name = "GitHub", Target = "https://github.com" } });

        Assert.True(File.Exists(LinkStore.FilePath(dir)));
    }

    [Fact]
    public void Save_UsesSnakeCaseOnDisk()
    {
        var dir = NewSettingsDir();

        LinkStore.Save(dir, new[] { new LinkEntry { Name = "GitHub", Target = "https://github.com", Icon = "auto" } });

        var json = File.ReadAllText(LinkStore.FilePath(dir));
        Assert.Contains("\"name\"", json);
        Assert.DoesNotContain("\"Name\"", json);
    }

    [Fact]
    public void HasQueryPlaceholder_DetectsPlaceholderCaseInsensitively()
    {
        Assert.True(new LinkEntry { Target = "https://x/{q}" }.HasQueryPlaceholder);
        Assert.True(new LinkEntry { Target = "https://x/{Q}" }.HasQueryPlaceholder);
        Assert.False(new LinkEntry { Target = "https://x/" }.HasQueryPlaceholder);
    }
}

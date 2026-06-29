using System.Collections.Generic;
using System.IO;
using System.Linq;
using Lookup.Models;
using Lookup.Services;
using Xunit;

namespace Lookup.Tests;

public class ScorerTests
{
    // A small fixture mirroring the shipped NAICS sample.
    private static SearchIndex BuildIndex()
    {
        var items = new List<LookupItem>
        {
            new() { Code = "541", Title = "Professional, Scientific, and Technical Services",
                    Category = "Sector", ParentCodes = { "54" }, Keywords = { "professional", "services" } },
            new() { Code = "5415", Title = "Computer Systems Design and Related Services",
                    ParentCodes = { "54", "541" }, Keywords = { "computer systems", "it services" },
                    Aliases = { "computer systems design" } },
            new() { Code = "541511", Title = "Custom Computer Programming Services",
                    Description = "Writing, modifying, testing, and supporting software.",
                    ParentCodes = { "54", "541", "5415" },
                    Keywords = { "software", "programming", "developer", "computer systems" },
                    Aliases = { "software development", "custom software" } },
            new() { Code = "541512", Title = "Computer Systems Design Services",
                    ParentCodes = { "54", "541", "5415" },
                    Keywords = { "computer systems", "systems design", "software" } },
            new() { Code = "541214", Title = "Payroll Services",
                    ParentCodes = { "54", "541", "5412" },
                    Keywords = { "payroll", "wages" }, Aliases = { "payroll processing" } },
            new() { Code = "511210", Title = "Software Publishers",
                    ParentCodes = { "51", "511" }, Keywords = { "software", "publishing", "saas" } },
            new() { Code = "111110", Title = "Soybean Farming",
                    ParentCodes = { "11", "111" }, Keywords = { "soybean", "farming", "crops" } },
        };

        var index = new SearchIndex();
        index.Build(new[] { new LookupDataset { Dataset = "naics", Version = "2022", Items = items } });
        return index;
    }

    private static List<string> Codes(IEnumerable<ScoredRecord> hits) =>
        hits.Select(h => h.Record.Item.Code).ToList();

    [Fact]
    public void ExactCode_RanksFirst()
    {
        var hits = BuildIndex().Search("541511", 20);
        Assert.Equal("541511", hits[0].Record.Item.Code);
    }

    [Fact]
    public void ExactCode_OutscoresCodePrefix()
    {
        var index = BuildIndex();
        var exact = index.Search("541511", 20)[0].Score;
        var prefix = index.Search("5415", 20).First(h => h.Record.Item.Code == "541511").Score;
        Assert.True(exact > prefix, $"exact {exact} should beat prefix {prefix}");
    }

    [Fact]
    public void CodePrefix_ReturnsAllUnderPrefix()
    {
        var codes = Codes(BuildIndex().Search("541", 20));
        Assert.Contains("541", codes);
        Assert.Contains("5415", codes);
        Assert.Contains("541511", codes);
        Assert.Contains("541512", codes);
        Assert.Contains("541214", codes);
        // A 51x code must not appear for a "541" prefix query.
        Assert.DoesNotContain("511210", codes);
        Assert.DoesNotContain("111110", codes);
    }

    [Fact]
    public void NumericQuery_DoesNotMatchProse()
    {
        // "111" is a code-like query: it should match the 111110 code, not random text.
        var codes = Codes(BuildIndex().Search("111", 20));
        Assert.Contains("111110", codes);
        Assert.DoesNotContain("541511", codes);
    }

    [Fact]
    public void Keyword_FindsRecords()
    {
        var codes = Codes(BuildIndex().Search("software", 20));
        Assert.Contains("541511", codes);
        Assert.Contains("511210", codes);
    }

    [Fact]
    public void MultiWordPhrase_RanksDesignServicesHigh()
    {
        var hits = BuildIndex().Search("computer systems", 20);
        // 5415 / 541512 both contain the phrase in title; one of them should lead.
        Assert.Contains(hits[0].Record.Item.Code, new[] { "5415", "541512" });
        Assert.Contains("541512", Codes(hits));
    }

    [Fact]
    public void Payroll_FindsPayrollServices()
    {
        Assert.Equal("541214", BuildIndex().Search("payroll", 20)[0].Record.Item.Code);
    }

    [Fact]
    public void Typo_StillMatches_ButBelowExact()
    {
        var index = BuildIndex();
        var typo = index.Search("sofware", 20);
        Assert.Contains("541511", Codes(typo));

        var exactScore = index.Search("software", 20).First(h => h.Record.Item.Code == "541511").Score;
        var typoScore = typo.First(h => h.Record.Item.Code == "541511").Score;
        Assert.True(typoScore < exactScore, $"typo {typoScore} should rank below exact {exactScore}");
    }

    [Fact]
    public void EmptyQuery_ReturnsNothing()
    {
        Assert.Empty(BuildIndex().Search("   ", 20));
    }

    [Fact]
    public void NoMatch_ReturnsEmpty()
    {
        Assert.Empty(BuildIndex().Search("zzzqqwx", 20));
    }

    [Fact]
    public void MaxResults_IsRespected()
    {
        Assert.True(BuildIndex().Search("services", 3).Count <= 3);
    }

    [Fact]
    public void Highlight_CoversQueryInTitle()
    {
        var hit = BuildIndex().Search("payroll", 20)[0];
        Assert.NotEmpty(hit.TitleHighlight); // "Payroll Services" contains "payroll"
    }
}

public class TextUtilsTests
{
    [Theory]
    [InlineData("software", "software", 0)]
    [InlineData("sofware", "software", 1)]
    [InlineData("", "abc", 3)]
    public void Levenshtein_Basic(string a, string b, int expected) =>
        Assert.Equal(expected, TextUtils.Levenshtein(a, b));

    [Fact]
    public void Similarity_IdenticalIsOne() => Assert.Equal(1.0, TextUtils.Similarity("abc", "abc"));

    [Fact]
    public void Normalize_CollapsesWhitespaceAndLowercases() =>
        Assert.Equal("computer systems", TextUtils.Normalize("  Computer   SYSTEMS "));

    [Fact]
    public void Tokenize_SplitsOnPunctuation() =>
        Assert.Equal(new[] { "a", "b", "c" }, TextUtils.Tokenize("a-b, c").ToArray());
}

public class DataLoaderTests
{
    private static string NewTempDir()
    {
        // Deterministic-ish unique dir without Random/DateTime (unavailable in some contexts).
        var dir = Path.Combine(Path.GetTempPath(), "lookup_test_" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void MissingDirectory_ReportsErrorNotThrow()
    {
        var result = DataLoader.Load(Path.Combine(Path.GetTempPath(), "does_not_exist_" + Path.GetRandomFileName()));
        Assert.False(result.HasData);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void InvalidJson_ReportsErrorNotThrow()
    {
        var dir = NewTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "bad.json"), "{ this is not valid json ");
            var result = DataLoader.Load(dir);
            Assert.False(result.HasData);
            Assert.Contains(result.Errors, e => e.Message.Contains("Invalid JSON"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ValidFile_LoadsAndSynthesizesMissingFields()
    {
        var dir = NewTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "mini.json"),
                """
                { "dataset": "mini", "version": "1",
                  "items": [ { "code": "A1", "title": "Alpha" } ] }
                """);
            var result = DataLoader.Load(dir);
            Assert.True(result.HasData);
            var item = result.Datasets.Single().Items.Single();
            Assert.Equal("A1", item.Code);
            Assert.Equal("mini:A1", item.Id);   // synthesized from dataset + code
            Assert.NotNull(item.Keywords);       // optional collections never null
            Assert.Empty(item.Keywords);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void NullArrayElements_AreStripped_AndDoNotAbortBuild()
    {
        var dir = NewTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "nulls.json"),
                """
                { "dataset": "nulls", "items": [
                  { "code": "X1", "title": "Has nulls", "keywords": ["foo", null, "", "  "] }
                ] }
                """);
            var result = DataLoader.Load(dir);
            Assert.True(result.HasData);
            var item = result.Datasets.Single().Items.Single();
            Assert.Equal(new[] { "foo" }, item.Keywords.ToArray());

            // And the index builds + searches without throwing on the cleaned record.
            var index = new SearchIndex();
            index.Build(result.Datasets);
            Assert.Equal("X1", index.Search("foo", 10).Single().Record.Item.Code);
        }
        finally { Directory.Delete(dir, true); }
    }
}

public class PluginConfigTests
{
    [Fact]
    public void SnakeCaseKeys_Bind()
    {
        var dir = Path.Combine(Path.GetTempPath(), "lookup_cfg_" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "config.json"),
                """
                { "max_results": 7, "enabled_datasets": ["naics"], "default_copy_field": "title" }
                """);
            var cfg = PluginConfig.Load(dir);
            Assert.Equal(7, cfg.MaxResults);
            Assert.Equal(new[] { "naics" }, cfg.EnabledDatasets?.ToArray());
            Assert.Equal("title", cfg.DefaultCopyField);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void MissingFile_UsesDefaults()
    {
        var cfg = PluginConfig.Load(Path.Combine(Path.GetTempPath(), "no_such_" + Path.GetRandomFileName()));
        Assert.Equal(15, cfg.MaxResults);
        Assert.Null(cfg.EnabledDatasets);
        Assert.Equal("code", cfg.DefaultCopyField);
    }
}

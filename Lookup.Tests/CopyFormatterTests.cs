using Lookup.Models;
using Lookup.Services;
using Xunit;

namespace Lookup.Tests;

public class CopyFormatterTests
{
    [Fact]
    public void CodeTitle_JoinsBothFields()
    {
        var item = new LookupItem { Code = "722513", Title = "Limited-Service Restaurants" };
        Assert.Equal("722513 - Limited-Service Restaurants", CopyFormatter.CodeTitle(item));
    }

    [Fact]
    public void CodeTitle_FallsBackToCodeWhenTitleMissing()
    {
        var item = new LookupItem { Code = "722513" };
        Assert.Equal("722513", CopyFormatter.CodeTitle(item));
    }

    [Fact]
    public void CodeTitle_FallsBackToTitleWhenCodeMissing()
    {
        var item = new LookupItem { Title = "Limited-Service Restaurants" };
        Assert.Equal("Limited-Service Restaurants", CopyFormatter.CodeTitle(item));
    }

    [Fact]
    public void FullDetails_AppendsDescriptionAfterBlankLine()
    {
        var item = new LookupItem
        {
            Code = "722513",
            Title = "Limited-Service Restaurants",
            Description = "Establishments primarily engaged in providing food services where patrons order and pay before eating.",
        };
        Assert.Equal(
            "722513 - Limited-Service Restaurants\n\nEstablishments primarily engaged in providing food services where patrons order and pay before eating.",
            CopyFormatter.FullDetails(item));
    }

    [Fact]
    public void FullDetails_FallsBackToCodeTitleWhenDescriptionMissing()
    {
        var item = new LookupItem { Code = "722513", Title = "Limited-Service Restaurants" };
        Assert.Equal("722513 - Limited-Service Restaurants", CopyFormatter.FullDetails(item));
    }
}

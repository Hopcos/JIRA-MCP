using JiraMcpServer.Jira.Validators;

namespace JiraMcpServer.Tests;

public class ConfluenceUrlTests
{
    [Fact]
    public void BareNumericIdIsAPageId()
    {
        var parts = ConfluenceUrl.TryParse("5233541311");
        Assert.True(parts.HasPageId);
        Assert.Equal("5233541311", parts.PageId);
        Assert.False(parts.HasSpaceKey);
    }

    [Fact]
    public void FullPageUrlCarriesIdSpaceAndTitle()
    {
        var parts = ConfluenceUrl.TryParse(
            "https://everymatrix.atlassian.net/wiki/spaces/PE/pages/5233541311/Basic+information");
        Assert.True(parts.HasPageId);
        Assert.Equal("5233541311", parts.PageId);
        Assert.True(parts.HasSpaceKey);
        Assert.Equal("PE", parts.SpaceKey);
        Assert.Equal("Basic information", parts.PageTitle);
    }

    [Fact]
    public void PageUrlWithoutTitleStillParsesIdAndSpace()
    {
        var parts = ConfluenceUrl.TryParse(
            "https://everymatrix.atlassian.net/wiki/spaces/PE/pages/5233541311");
        Assert.True(parts.HasPageId);
        Assert.Equal("5233541311", parts.PageId);
        Assert.Equal("PE", parts.SpaceKey);
    }

    [Fact]
    public void TrailingSlashIsTolerated()
    {
        var parts = ConfluenceUrl.TryParse(
            "https://everymatrix.atlassian.net/wiki/spaces/PE/pages/5233541311/Basic+information/");
        Assert.True(parts.HasPageId);
        Assert.Equal("5233541311", parts.PageId);
        Assert.Equal("PE", parts.SpaceKey);
    }

    [Fact]
    public void ViewPageActionQueryCarriesPageId()
    {
        var parts = ConfluenceUrl.TryParse(
            "https://everymatrix.atlassian.net/wiki/pages/viewpage.action?pageId=5233541311");
        Assert.True(parts.HasPageId);
        Assert.Equal("5233541311", parts.PageId);
    }

    [Theory]
    [InlineData("https://everymatrix.atlassian.net/pages/viewpage.action?pageId=123")]
    [InlineData("https://everymatrix.atlassian.net/wiki/pages/viewpage.action?spaceKey=PE&pageId=456")]
    public void ViewPageActionVariantsCarryPageId(string url)
    {
        var parts = ConfluenceUrl.TryParse(url);
        Assert.True(parts.HasPageId);
    }

    [Fact]
    public void SpaceOverviewCarriesOnlySpace()
    {
        var parts = ConfluenceUrl.TryParse("https://everymatrix.atlassian.net/wiki/spaces/PE/overview");
        Assert.False(parts.HasPageId);
        Assert.True(parts.HasSpaceKey);
        Assert.Equal("PE", parts.SpaceKey);
    }

    [Fact]
    public void UnrecognizedInputYieldsEmptyParts()
    {
        var parts = ConfluenceUrl.TryParse("https://example.com/some/other/page");
        Assert.False(parts.HasPageId);
        Assert.False(parts.HasSpaceKey);
    }

    [Fact]
    public void EmptyOrNullInputYieldsEmptyParts()
    {
        Assert.False(ConfluenceUrl.TryParse(null).HasPageId);
        Assert.False(ConfluenceUrl.TryParse("   ").HasSpaceKey);
    }
}

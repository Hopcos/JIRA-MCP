using JiraMcpServer.Tools.Permissions;

namespace JiraMcpServer.Tests;

public class PermissionsTests
{
    [Fact]
    public void ReadCategoryIncludesConfluenceTools()
    {
        var allowlist = Permissions.ParseTools("read");
        foreach (var tool in new[] { "confluence_get_page", "confluence_search", "confluence_list_spaces", "confluence_get_space" })
        {
            Assert.Contains(tool, allowlist);
        }
    }

    [Fact]
    public void ConfluenceToolsAreIndividuallyAddressable()
    {
        Assert.Contains("confluence_search", Permissions.AllTools);
        Assert.Contains("confluence_search", Permissions.ParseTools("confluence_search"));
    }

    [Fact]
    public void ConfluenceToolsAreNotInWriteCategories()
    {
        Assert.DoesNotContain("confluence_get_page", Permissions.CreateTools);
        Assert.DoesNotContain("confluence_get_page", Permissions.UpdateTools);
        Assert.DoesNotContain("confluence_get_page", Permissions.DeleteTools);
    }

    [Fact]
    public void EachConfluenceToolMapsToExactlyOneCategory()
    {
        foreach (var tool in Permissions.ReadTools.Where(t => t.StartsWith("confluence")))
        {
            var categoryCount =
                (Permissions.ReadTools.Contains(tool) ? 1 : 0) +
                (Permissions.CreateTools.Contains(tool) ? 1 : 0) +
                (Permissions.UpdateTools.Contains(tool) ? 1 : 0) +
                (Permissions.DeleteTools.Contains(tool) ? 1 : 0);
            Assert.Equal(1, categoryCount);
        }
    }
}

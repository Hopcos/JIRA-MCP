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
    public void ConfluenceWriteToolsAreInCreateAndUpdateCategories()
    {
        Assert.Contains("confluence_create_page", Permissions.CreateTools);
        Assert.Contains("confluence_update_page", Permissions.UpdateTools);
        Assert.DoesNotContain("confluence_create_page", Permissions.ReadTools);
        Assert.DoesNotContain("confluence_update_page", Permissions.ReadTools);

        var read = Permissions.ParseTools("read");
        Assert.DoesNotContain("confluence_create_page", read);
        Assert.DoesNotContain("confluence_update_page", read);

        var write = Permissions.ParseTools("write");
        Assert.Contains("confluence_create_page", write);
        Assert.Contains("confluence_update_page", write);
        Assert.Contains("confluence_update_page", Permissions.ParseTools("update"));
    }

    [Fact]
    public void EveryRegisteredConfluenceToolIsMappedToExactlyOneCategory()
    {
        var registered = typeof(JiraMcpServer.Tools.Confluence.ConfluenceTools)
            .GetMethods()
            .Select(m => m.GetCustomAttributes(typeof(ModelContextProtocol.Server.McpServerToolAttribute), false)
                .Cast<ModelContextProtocol.Server.McpServerToolAttribute>().FirstOrDefault()?.Name)
            .Where(n => n is not null)
            .ToList();
        Assert.NotEmpty(registered);

        foreach (var tool in registered)
        {
            var categoryCount =
                (Permissions.ReadTools.Contains(tool!) ? 1 : 0) +
                (Permissions.CreateTools.Contains(tool!) ? 1 : 0) +
                (Permissions.UpdateTools.Contains(tool!) ? 1 : 0) +
                (Permissions.DeleteTools.Contains(tool!) ? 1 : 0);
            Assert.Equal(1, categoryCount);
        }
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

using JiraMcpServer.Jira.Formatters;

namespace JiraMcpServer.Tests;

public class HtmlToTextTests
{
    [Fact]
    public void StripsTagsAndKeepText()
    {
        Assert.Equal("Hello world", HtmlToText.Convert("<p>Hello world</p>"));
    }

    [Fact]
    public void DecodesEntities()
    {
        Assert.Equal("QA & Stage", HtmlToText.Convert("<p>QA &amp; Stage</p>"));
        Assert.Equal("a < b", HtmlToText.Convert("a &lt; b"));
        Assert.Equal("A & B", HtmlToText.Convert("A &#38; B"));
    }

    [Fact]
    public void DropsScriptBodies()
    {
        var html = "<p>safe</p><script>alert('x')</script><p>also safe</p>";
        Assert.DoesNotContain("alert", HtmlToText.Convert(html));
        Assert.Contains("safe", HtmlToText.Convert(html));
    }

    [Fact]
    public void PreservesPreBlocks()
    {
        var html = "<pre>line1\nline2</pre>";
        Assert.Equal("line1\nline2", HtmlToText.Convert(html));
    }

    [Fact]
    public void TableCellsSeparateWithPipes()
    {
        var html = "<table><tr><td>a</td><td>b</td></tr></table>";
        var text = HtmlToText.Convert(html);
        Assert.Contains("a", text);
        Assert.Contains("b", text);
    }

    [Fact]
    public void EmptyInputYieldsEmpty()
    {
        Assert.Equal("", HtmlToText.Convert(null));
        Assert.Equal("", HtmlToText.Convert(""));
    }
}

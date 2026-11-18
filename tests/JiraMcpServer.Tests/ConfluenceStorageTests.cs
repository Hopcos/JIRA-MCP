using JiraMcpServer.Jira.Formatters;

namespace JiraMcpServer.Tests;

public class ConfluenceStorageTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n  ")]
    public void EmptyInputYieldsEmptyString(string? input) =>
        Assert.Equal("", ConfluenceStorage.FromMarkdown(input));

    [Fact]
    public void HeadingsAndParagraphs()
    {
        var html = ConfluenceStorage.FromMarkdown("# Title\n\nFirst line\nsecond line\n\n### Sub");
        Assert.Equal("<h1>Title</h1><p>First line<br/>second line</p><h3>Sub</h3>", html);
    }

    [Fact]
    public void InlineFormatting()
    {
        var html = ConfluenceStorage.FromMarkdown("a **bold** and *it* and ~~old~~ and `code` and snake_case_name");
        Assert.Equal(
            "<p>a <strong>bold</strong> and <em>it</em> and <del>old</del> and <code>code</code> and snake_case_name</p>",
            html);
    }

    [Fact]
    public void LiteralMarkupIsEscaped()
    {
        var html = ConfluenceStorage.FromMarkdown("1 < 2 & <script>alert(1)</script>");
        Assert.Equal("<p>1 &lt; 2 &amp; &lt;script&gt;alert(1)&lt;/script&gt;</p>", html);
    }

    [Fact]
    public void CodeSpanContentIsNotInterpreted()
    {
        Assert.Equal("<p><code>**x** &lt;b&gt;</code></p>", ConfluenceStorage.FromMarkdown("`**x** <b>`"));
    }

    [Fact]
    public void NonAsciiTextPassesThroughUnescaped()
    {
        Assert.Equal("<p>你好 café</p>", ConfluenceStorage.FromMarkdown("你好 café"));
    }

    [Fact]
    public void Links()
    {
        Assert.Equal(
            "<p><a href=\"https://x.test/a_b?q=1&amp;r=2\">the <strong>docs</strong></a></p>",
            ConfluenceStorage.FromMarkdown("[the **docs**](https://x.test/a_b?q=1&r=2)"));
    }

    [Fact]
    public void UnsafeLinkSchemeKeepsTextDropsLink()
    {
        var html = ConfluenceStorage.FromMarkdown("[click](javascript:alert(1))");
        Assert.DoesNotContain("href", html);
        Assert.Contains("click", html);
    }

    [Fact]
    public void BulletAndOrderedLists()
    {
        Assert.Equal("<ul><li>a</li><li>b</li></ul>", ConfluenceStorage.FromMarkdown("- a\n- b"));
        Assert.Equal("<ol><li>one</li><li>two</li></ol>", ConfluenceStorage.FromMarkdown("1. one\n2. two"));
    }

    [Fact]
    public void NestedLists()
    {
        var html = ConfluenceStorage.FromMarkdown("- a\n  - a1\n  - a2\n- b");
        Assert.Equal("<ul><li>a<ul><li>a1</li><li>a2</li></ul></li><li>b</li></ul>", html);
    }

    [Fact]
    public void FencedCodeBlockBecomesCodeMacro()
    {
        var html = ConfluenceStorage.FromMarkdown("```csharp\nvar x = 1 < 2;\n\n# not a heading\n```\nafter");
        Assert.Equal(
            "<ac:structured-macro ac:name=\"code\" ac:schema-version=\"1\">" +
            "<ac:parameter ac:name=\"language\">csharp</ac:parameter>" +
            "<ac:plain-text-body><![CDATA[var x = 1 < 2;\n\n# not a heading]]></ac:plain-text-body>" +
            "</ac:structured-macro><p>after</p>",
            html);
    }

    [Fact]
    public void CodeBlockSplitsCdataTerminator()
    {
        var html = ConfluenceStorage.FromMarkdown("```\na]]>b\n```");
        Assert.Contains("<![CDATA[a]]]]><![CDATA[>b]]>", html);
    }

    [Fact]
    public void UnterminatedFenceDoesNotLoopOrDropContent()
    {
        var html = ConfluenceStorage.FromMarkdown("```\ncode");
        Assert.Contains("<![CDATA[code]]>", html);
    }

    [Fact]
    public void Table()
    {
        var html = ConfluenceStorage.FromMarkdown("| Name | Qty |\n|------|----:|\n| a | 1 |\n| b \\| c | **2** |");
        Assert.Equal(
            "<table><tbody>" +
            "<tr><th><p>Name</p></th><th><p>Qty</p></th></tr>" +
            "<tr><td><p>a</p></td><td><p>1</p></td></tr>" +
            "<tr><td><p>b | c</p></td><td><p><strong>2</strong></p></td></tr>" +
            "</tbody></table>",
            html);
    }

    [Fact]
    public void BlockquoteAndRule()
    {
        Assert.Equal("<blockquote><p>quoted</p></blockquote><hr/>", ConfluenceStorage.FromMarkdown("> quoted\n\n---"));
    }

    [Fact]
    public void WindowsLineEndings()
    {
        Assert.Equal("<h2>T</h2><ul><li>a</li><li>b</li></ul>", ConfluenceStorage.FromMarkdown("## T\r\n\r\n- a\r\n- b\r\n"));
    }

    [Fact]
    public void ListInterruptsParagraph()
    {
        Assert.Equal("<p>intro</p><ul><li>a</li></ul>", ConfluenceStorage.FromMarkdown("intro\n- a"));
    }
}

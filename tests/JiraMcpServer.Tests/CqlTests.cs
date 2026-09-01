using JiraMcpServer.Jira.Validators;

namespace JiraMcpServer.Tests;

public class CqlTests
{
    [Fact]
    public void EscapeValueQuotesStringsAndEscapesInnerQuotes()
    {
        Assert.Equal("\"a'\"", Cql.EscapeValue("a'"));
        Assert.Equal("\"a\\\"b\"", Cql.EscapeValue("a\"b"));
        Assert.Equal("\"Basic information\"", Cql.EscapeValue("Basic information"));
    }

    [Fact]
    public void EscapeValueHandlesNonStrings()
    {
        Assert.Equal("true", Cql.EscapeValue(true));
        Assert.Equal("42", Cql.EscapeValue(42));
        Assert.Equal("EMPTY", Cql.EscapeValue(null));
    }

    [Fact]
    public void BuildAndsClauses()
    {
        var query = Cql.Build(new[] { "type = page", "space = 'PE'", "title ~ 'x'" });
        Assert.Equal("(type = page) AND (space = 'PE') AND (title ~ 'x')", query);
    }

    [Fact]
    public void BuildSingleClauseIsVerbatim()
    {
        Assert.Equal("type = page", Cql.Build(new[] { "type = page" }));
    }

    [Fact]
    public void BuildDropsEmptyClauses()
    {
        Assert.Equal("type = page", Cql.Build(new[] { "", "type = page", "   " }));
    }

    [Fact]
    public void BuildEmptyYieldsEmptyString()
    {
        Assert.Equal("", Cql.Build(new string[] { }));
    }
}

using ChatClient.Api.Client.Components;

namespace ChatClient.Tests;

public sealed class WorkflowSourceCodeSyntaxHighlighterTests
{
    [Fact]
    public void ToHighlightedHtml_ClassifiesKeywordsStringsNumbersCommentsIdentifiersAndPunctuation()
    {
        const string source = """
            return "value"; // comment
            count = 42;
            """;

        var html = WorkflowSourceCodeSyntaxHighlighter.ToHighlightedHtml(source);

        Assert.Contains("<span class=\"tok-keyword\">return</span>", html, StringComparison.Ordinal);
        Assert.Contains("<span class=\"tok-identifier\">count</span>", html, StringComparison.Ordinal);
        Assert.Contains("<span class=\"tok-string\">&quot;value&quot;</span>", html, StringComparison.Ordinal);
        Assert.Contains("<span class=\"tok-punctuation\">=</span>", html, StringComparison.Ordinal);
        Assert.Contains("<span class=\"tok-comment\">// comment</span>", html, StringComparison.Ordinal);
        Assert.Contains("<span class=\"tok-number\">42</span>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void ToHighlightedHtml_EncodesHtmlSensitiveCharacters()
    {
        const string source = """
            var html = "<tag>&\"";
            """;

        var html = WorkflowSourceCodeSyntaxHighlighter.ToHighlightedHtml(source);

        Assert.Contains("&lt;tag&gt;", html, StringComparison.Ordinal);
        Assert.Contains("&amp;", html, StringComparison.Ordinal);
        Assert.Contains("&quot;", html, StringComparison.Ordinal);
    }

    [Fact]
    public void ToHighlightedHtml_ReturnsPlaceholderForEmptyInput()
    {
        var html = WorkflowSourceCodeSyntaxHighlighter.ToHighlightedHtml(string.Empty);

        Assert.Equal("&nbsp;", html);
    }

    [Fact]
    public void ToHighlightedHtml_DoesNotThrowForIncompleteInput()
    {
        const string source = """
            var text = "
            """;

        var exception = Record.Exception(() => WorkflowSourceCodeSyntaxHighlighter.ToHighlightedHtml(source));

        Assert.Null(exception);
    }

    [Fact]
    public void ToHighlightedHtml_MarksBadTokensAsErrors()
    {
        var html = WorkflowSourceCodeSyntaxHighlighter.ToHighlightedHtml("`");

        Assert.Contains("tok-error", html, StringComparison.Ordinal);
    }
}

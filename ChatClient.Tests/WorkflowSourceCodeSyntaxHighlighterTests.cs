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

    [Fact]
    public void ToHighlightedHtml_HighlightsAgentDslMethodsAndNames()
    {
        const string source = """
            var workflow = WorkflowDefinitionBuilder
                .New("philosopher-battle-group-chat", "Philosopher Battle Group Chat")
                .Agent("host", agent => agent
                    .UseDraft(
                        AgentDefinitionBuilder
                            .New("Debate Host", "host")
                            .BuildDescription()))
                .AgentFromSaved("Immanuel Kant", agent => agent
                    .Id("kant"))
                .UseGroupChat(groupChat => groupChat
                    .Participants("host", "kant")
                    .UseCustomManager("philosopher-debate", maximumIterations: 10))
                .UseHandoff(handoff => handoff
                    .StartWith("host")
                    .Handoff("host", "kant", "open with Kant")
                    .Fallback("kant", "host"))
                .Build();
            """;

        var html = WorkflowSourceCodeSyntaxHighlighter.ToHighlightedHtml(source);

        Assert.Contains("<span class=\"tok-agent-registration\">Agent</span>", html, StringComparison.Ordinal);
        Assert.Contains("<span class=\"tok-agent-registration\">AgentFromSaved</span>", html, StringComparison.Ordinal);
        Assert.Contains("<span class=\"tok-agent-registration\">Id</span>", html, StringComparison.Ordinal);
        Assert.Contains("<span class=\"tok-agent-factory\">UseDraft</span>", html, StringComparison.Ordinal);
        Assert.Contains("<span class=\"tok-agent-factory\">New</span>", html, StringComparison.Ordinal);
        Assert.Contains("<span class=\"tok-agent-reference\">Participants</span>", html, StringComparison.Ordinal);
        Assert.Contains("<span class=\"tok-agent-reference\">StartWith</span>", html, StringComparison.Ordinal);
        Assert.Contains("<span class=\"tok-agent-reference\">Handoff</span>", html, StringComparison.Ordinal);
        Assert.Contains("<span class=\"tok-agent-reference\">Fallback</span>", html, StringComparison.Ordinal);
        Assert.Contains("<span class=\"tok-manager-config\">UseCustomManager</span>", html, StringComparison.Ordinal);
        Assert.Contains("<span class=\"tok-agent-id\">&quot;host&quot;</span>", html, StringComparison.Ordinal);
        Assert.Contains("<span class=\"tok-agent-id\">&quot;kant&quot;</span>", html, StringComparison.Ordinal);
        Assert.Contains("<span class=\"tok-agent-display-name\">&quot;Debate Host&quot;</span>", html, StringComparison.Ordinal);
        Assert.Contains("<span class=\"tok-agent-display-name\">&quot;Immanuel Kant&quot;</span>", html, StringComparison.Ordinal);
        Assert.Contains("<span class=\"tok-manager-id\">&quot;philosopher-debate&quot;</span>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void ToHighlightedHtml_LeavesWorkflowFactoryLiteralsAsRegularStrings()
    {
        const string source = """
            var workflow = WorkflowDefinitionBuilder
                .New("workflow-id", "Workflow Name")
                .Agent("host", agent => agent
                    .UseDraft(
                        AgentDefinitionBuilder
                            .New("Debate Host", "host")
                            .BuildDescription()))
                .Build();
            """;

        var html = WorkflowSourceCodeSyntaxHighlighter.ToHighlightedHtml(source);

        Assert.Contains("<span class=\"tok-string\">&quot;workflow-id&quot;</span>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<span class=\"tok-agent-id\">&quot;workflow-id&quot;</span>", html, StringComparison.Ordinal);
        Assert.Contains("<span class=\"tok-agent-display-name\">&quot;Debate Host&quot;</span>", html, StringComparison.Ordinal);
        Assert.Contains("<span class=\"tok-agent-id\">&quot;host&quot;</span>", html, StringComparison.Ordinal);
    }
}

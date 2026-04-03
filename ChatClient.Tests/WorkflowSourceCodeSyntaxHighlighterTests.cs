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
                .New("review-panel-group-chat", "Review Panel Group Chat")
                .Agent("moderator", agent => agent
                    .UseDraft(
                        AgentDefinitionBuilder
                            .New("Review Moderator", "moderator")
                            .BuildDescription()))
                .Agent("reviewer", agent => agent
                    .FromSavedAgent("Saved Reviewer"))
                .UseGroupChat(groupChat => groupChat
                    .Participants("moderator", "reviewer")
                    .UseProgrammableManager(manager => manager
                        .MaximumIterations(10)
                        .Program(GroupChatManagerPrograms.PrefixCycleSuffix(
                            prefix: new[] { "moderator" },
                            cycle: new[] { "reviewer" },
                            suffix: new[] { "reviewer" }))))
                .UseHandoff(handoff => handoff
                    .StartWith("moderator")
                    .Handoff("moderator", "reviewer", "open review")
                    .Fallback("reviewer", "moderator"))
                .Build();
            """;

        var html = WorkflowSourceCodeSyntaxHighlighter.ToHighlightedHtml(source);

        Assert.Contains("<span class=\"tok-agent-registration\">Agent</span>", html, StringComparison.Ordinal);
        Assert.Contains("<span class=\"tok-agent-factory\">FromSavedAgent</span>", html, StringComparison.Ordinal);
        Assert.Contains("<span class=\"tok-agent-factory\">UseDraft</span>", html, StringComparison.Ordinal);
        Assert.Contains("<span class=\"tok-agent-factory\">New</span>", html, StringComparison.Ordinal);
        Assert.Contains("<span class=\"tok-agent-reference\">Participants</span>", html, StringComparison.Ordinal);
        Assert.Contains("<span class=\"tok-agent-reference\">StartWith</span>", html, StringComparison.Ordinal);
        Assert.Contains("<span class=\"tok-agent-reference\">Handoff</span>", html, StringComparison.Ordinal);
        Assert.Contains("<span class=\"tok-agent-reference\">Fallback</span>", html, StringComparison.Ordinal);
        Assert.Contains("<span class=\"tok-manager-config\">UseProgrammableManager</span>", html, StringComparison.Ordinal);
        Assert.Contains("<span class=\"tok-manager-config\">Program</span>", html, StringComparison.Ordinal);
        Assert.Contains("<span class=\"tok-agent-id\">&quot;moderator&quot;</span>", html, StringComparison.Ordinal);
        Assert.Contains("<span class=\"tok-agent-id\">&quot;reviewer&quot;</span>", html, StringComparison.Ordinal);
        Assert.Contains("<span class=\"tok-agent-display-name\">&quot;Review Moderator&quot;</span>", html, StringComparison.Ordinal);
        Assert.Contains("<span class=\"tok-agent-display-name\">&quot;Saved Reviewer&quot;</span>", html, StringComparison.Ordinal);
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

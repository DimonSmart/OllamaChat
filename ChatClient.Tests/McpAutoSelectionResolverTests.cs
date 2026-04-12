using System.Text.Json;
using ChatClient.Api.Services;

namespace ChatClient.Tests;

public class McpAutoSelectionResolverTests
{
    [Fact]
    public void ResolveQualifiedFunctionNames_PreservesIndexRanking()
    {
        var tools = new[]
        {
            CreateTool("browser:open", "browser", "open", "Browser open tool."),
            CreateTool("built-in-user-profile-prefs:prefs_get", "Built-in User Profile Prefs MCP Server", "prefs_get", "Gets the current user's display name for personalization.")
        };

        var resolved = McpAutoSelectionResolver.ResolveQualifiedFunctionNames(
            ["built-in-user-profile-prefs:prefs_get", "browser:open"],
            tools,
            userQuery: "\u043a\u0430\u043a\u043e\u0439 \u0443 \u043c\u0435\u043d\u044f \u043f\u0440\u043e\u0444\u0438\u043b\u044c",
            maxCount: 2);

        Assert.Equal(
            ["built-in-user-profile-prefs:prefs_get", "browser:open"],
            resolved);
    }

    [Fact]
    public void ResolveQualifiedFunctionNames_GreetingPromotesProfileLookup()
    {
        var tools = new[]
        {
            CreateTool("browser:open", "browser", "open", "Browser open tool."),
            CreateTool("built-in-user-profile-prefs:prefs_get", "Built-in User Profile Prefs MCP Server", "prefs_get", "Gets the current user's display name for greeting and personalization.")
        };

        var resolved = McpAutoSelectionResolver.ResolveQualifiedFunctionNames(
            ["browser:open"],
            tools,
            userQuery: "\u041f\u0440\u0438\u0432\u0435\u0442, LLM",
            maxCount: 1);

        Assert.Equal(["built-in-user-profile-prefs:prefs_get"], resolved);
    }

    private static AppToolDescriptor CreateTool(
        string qualifiedName,
        string serverName,
        string toolName,
        string description)
    {
        using var document = JsonDocument.Parse("""{"type":"object","properties":{}}""");

        return new AppToolDescriptor(
            QualifiedName: qualifiedName,
            ServerName: serverName,
            ToolName: toolName,
            DisplayName: toolName,
            Description: description,
            InputSchema: document.RootElement.Clone(),
            OutputSchema: null,
            MayRequireUserInput: false,
            ReadOnlyHint: true,
            DestructiveHint: false,
            IdempotentHint: true,
            OpenWorldHint: false,
            ExecuteAsync: static (_, _) => Task.FromResult<object>("ok"),
            BaseQualifiedName: qualifiedName,
            BaseServerName: serverName);
    }
}

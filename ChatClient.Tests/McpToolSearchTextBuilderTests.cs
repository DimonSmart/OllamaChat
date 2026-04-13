using ChatClient.Api.Services;
using ChatClient.Api.Services.BuiltIn;
using System.Text.Json;

namespace ChatClient.Tests;

public class McpToolSearchTextBuilderTests
{
    [Fact]
    public void Build_UserProfileTool_IncludesNameAndSchemaHints()
    {
        var snapshot = UserProfilePreferencesRuntime.CreateSnapshot(
            UserProfilePreferencesDocument.CreateDefault(),
            useDefaultWhenMissing: true);
        var description = UserProfilePreferencesRuntime.BuildPrefsGetDescription(snapshot);
        var inputSchema = UserProfilePreferencesRuntime.BuildPrefsGetInputSchema(snapshot);

        var searchableText = McpToolSearchTextBuilder.Build(
            "Built-in User Profile Prefs MCP Server",
            "prefs_get",
            description,
            inputSchema,
            outputSchema: null);

        Assert.Contains("current user's display name", searchableText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("preferred user name", searchableText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("displayName", searchableText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("preferred_name", searchableText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("userName", searchableText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Build_ArbitrarySchema_IncludesFieldDescriptionsAndEnumValues()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "type": "object",
              "properties": {
                "key": {
                  "type": "string",
                  "description": "Current user profile field name.",
                  "enum": ["displayName", "timezone"]
                }
              },
              "required": ["key"]
            }
            """);

        var searchableText = McpToolSearchTextBuilder.Build(
            "profile",
            "prefs_get",
            "Gets the current user profile value.",
            document.RootElement.Clone(),
            outputSchema: null);

        Assert.Contains("Current user profile field name", searchableText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("displayName", searchableText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("timezone", searchableText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Required: key", searchableText, StringComparison.OrdinalIgnoreCase);
    }
}

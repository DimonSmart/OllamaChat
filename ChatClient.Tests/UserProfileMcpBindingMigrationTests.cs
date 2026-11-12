using ChatClient.Api.Services;
using ChatClient.Domain.Models;
using System.Text.Json;

namespace ChatClient.Tests;

public sealed class UserProfileMcpBindingMigrationTests
{
    private static readonly Guid ProfileServerId = Guid.Parse("c8c4a3cf-e2d5-4f4d-9a6f-4504e322a2b3");
    private const string ProfileServerName = "Built-in User Memory MCP Server";

    [Fact]
    public void SelectedReadTools_MigrateWithoutGrantingWriteAccess()
    {
        var binding = CreateProfileBinding("prefs_get", "prefs_get_all");

        var resolved = McpBindingToolSelectionResolver.FilterAvailableTools([binding], AllTools());

        Assert.Equal(["profile_get", "profile_get_all"],
            resolved.Select(static tool => tool.ToolName).ToArray());
        Assert.DoesNotContain(resolved, static tool => tool.ToolName == "profile_set");
        Assert.Equal(["profile_get", "profile_get_all"], binding.Clone().SelectedTools);
    }

    [Fact]
    public void OldWritePermission_MigratesToProfileSet()
    {
        var binding = CreateProfileBinding("prefs_set");

        Assert.Equal("profile_set",
            Assert.Single(McpBindingToolSelectionResolver.FilterAvailableTools([binding], AllTools())).ToolName);
    }

    [Fact]
    public void DeleteOnlyAndFreeTextMemoryPermissions_DoNotBecomeWritePermissions()
    {
        var binding = CreateProfileBinding(
            "prefs_delete", "prefs_reset_all", "memory_remember", "memory_search", "memory_list", "memory_forget");

        Assert.Empty(binding.GetCompatibleSelectedTools());
        Assert.Empty(McpBindingToolSelectionResolver.FilterAvailableTools([binding], AllTools()));
        Assert.Empty(binding.Clone().SelectedTools);
    }

    [Fact]
    public void SelectAllTools_ContinuesToSelectThreeTools()
    {
        var binding = CreateProfileBinding("prefs_delete");
        binding.SelectAllTools = true;

        Assert.Equal(3, McpBindingToolSelectionResolver.FilterAvailableTools([binding], AllTools()).Count);
    }

    [Fact]
    public void AnotherServer_IsNotAffectedByProfilePermissionMigration()
    {
        var binding = new McpServerSessionBinding
        {
            ServerName = "External MCP",
            SelectAllTools = false,
            SelectedTools = ["prefs_get"]
        };

        Assert.Equal("prefs_get", Assert.Single(binding.GetCompatibleSelectedTools()));
        Assert.Equal("prefs_get", Assert.Single(binding.Clone().SelectedTools));
    }

    private static McpServerSessionBinding CreateProfileBinding(params string[] tools) => new()
    {
        ServerId = ProfileServerId,
        ServerName = ProfileServerName,
        SelectAllTools = false,
        SelectedTools = [.. tools]
    };

    private static AppToolDescriptor[] AllTools() =>
    [
        CreateTool("profile_get"),
        CreateTool("profile_get_all"),
        CreateTool("profile_set")
    ];

    private static AppToolDescriptor CreateTool(string name) =>
        new(
            QualifiedName: $"{ProfileServerName}:{name}",
            ServerName: ProfileServerName,
            ToolName: name,
            DisplayName: name,
            Description: string.Empty,
            InputSchema: JsonSerializer.SerializeToElement(new { type = "object" }),
            OutputSchema: null,
            MayRequireUserInput: false,
            ReadOnlyHint: name != "profile_set",
            DestructiveHint: false,
            IdempotentHint: true,
            OpenWorldHint: false,
            ExecuteAsync: static (_, _) => Task.FromResult<object>(string.Empty),
            BaseQualifiedName: $"{ProfileServerName}:{name}",
            BaseServerName: ProfileServerName,
            BindingId: null,
            BindingDisplayName: null);
}

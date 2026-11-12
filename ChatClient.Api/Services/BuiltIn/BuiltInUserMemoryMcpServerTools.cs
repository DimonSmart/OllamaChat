using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.Json;

namespace ChatClient.Api.Services.BuiltIn;

[McpServerToolType]
public sealed class BuiltInUserMemoryMcpServerTools
{
    public static IBuiltInMcpServerDescriptor Descriptor { get; } = new BuiltInMcpServerDescriptor(
        id: Guid.Parse("c8c4a3cf-e2d5-4f4d-9a6f-4504e322a2b3"),
        key: "built-in-user-memory",
        name: "Built-in User Memory MCP Server",
        description: "Get or update explicitly stored current-user profile fields.",
        registerTools: static builder => builder.WithTools<BuiltInUserMemoryMcpServerTools>());

    [McpServerTool(Name = "profile_get", ReadOnly = true, UseStructuredContent = true)]
    [Description("Get a stored value from the current user's profile by key.")]
    public static async Task<object> GetAsync(
        [Description("Profile key.")] string key,
        CancellationToken cancellationToken = default)
    {
        var normalizedKey = key?.Trim();
        if (string.IsNullOrEmpty(normalizedKey))
            throw new ArgumentException("User profile key must not be empty.", nameof(key));

        var (exists, value) = await UserProfileStore.Default.GetAsync(normalizedKey, cancellationToken);
        return JsonSerializer.SerializeToElement(new { key = normalizedKey, exists, value });
    }

    [McpServerTool(Name = "profile_get_all", ReadOnly = true, UseStructuredContent = true)]
    [Description("Get all stored values from the current user's profile.")]
    public static async Task<object> GetAllAsync(CancellationToken cancellationToken = default) =>
        new { values = await UserProfileStore.Default.GetAllAsync(cancellationToken) };

    [McpServerTool(Name = "profile_set", UseStructuredContent = true)]
    [Description("Set a user profile value. An empty or null value deletes the key.")]
    public static async Task<object> SetAsync(
        [Description("Profile key.")] string key,
        [Description("String value; empty or null deletes the key.")] string? value,
        CancellationToken cancellationToken = default)
    {
        var result = await UserProfileStore.Default.SetAsync(key, value, cancellationToken);
        return JsonSerializer.SerializeToElement(new { key = result.Key, value = result.Value, deleted = result.Deleted });
    }
}

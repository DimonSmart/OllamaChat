using ModelContextProtocol.Server;
using System.ComponentModel;

namespace ChatClient.Api.Services.BuiltIn;

[McpServerToolType]
public sealed class BuiltInUserMemoryMcpServerTools
{
    public static IBuiltInMcpServerDescriptor Descriptor { get; } = new BuiltInMcpServerDescriptor(
        id: Guid.Parse("c8c4a3cf-e2d5-4f4d-9a6f-4504e322a2b3"),
        key: "built-in-user-memory",
        name: "Built-in User Memory MCP Server",
        description: "Explicitly reads and writes durable user preferences and learned facts.",
        registerTools: static builder => builder.WithTools<BuiltInUserMemoryMcpServerTools>(),
        descriptionFactory: static () => UserProfilePreferencesRuntime.BuildServerDescription(UserProfilePreferencesStore.GetSnapshot()));

    [McpServerTool(Name = "prefs_get", ReadOnly = true, UseStructuredContent = true)]
    [Description("Reads one configured user preference. This operation never asks the user and never changes stored data.")]
    public static async Task<object> GetPreferenceAsync(
        [Description("Configured preference key or alias.")] string key,
        CancellationToken cancellationToken = default)
    {
        var document = await UserProfilePreferencesStore.GetAsync(cancellationToken);
        var snapshot = UserProfilePreferencesRuntime.CreateSnapshot(document);
        if (!snapshot.TryResolveKey(key, out var normalizedKey))
        {
            throw new InvalidOperationException($"unknown_key:{key?.Trim() ?? string.Empty}");
        }

        var exists = snapshot.TryGetStoredValue(normalizedKey, out var value);
        return new { key = normalizedKey, exists, value = exists ? value : null };
    }

    [McpServerTool(Name = "prefs_set", UseStructuredContent = true)]
    [Description("Stores or replaces one configured user preference.")]
    public static async Task<object> SetPreferenceAsync(
        [Description("Configured preference key or alias.")] string key,
        [Description("Preference value to store.")] string value,
        CancellationToken cancellationToken = default)
    {
        var stored = await UserProfilePreferencesStore.SetValueAsync(key, value, cancellationToken);
        return new { key = stored.Key, value = stored.Value };
    }

    [McpServerTool(Name = "prefs_get_all", ReadOnly = true, UseStructuredContent = true)]
    [Description("Lists configured preference definitions together with their currently stored values.")]
    public static async Task<object> GetAllPreferencesAsync(CancellationToken cancellationToken = default)
    {
        var document = await UserProfilePreferencesStore.GetAsync(cancellationToken);
        var snapshot = UserProfilePreferencesRuntime.CreateSnapshot(document);
        return new
        {
            definitions = snapshot.Definitions.Select(static definition => new
            {
                key = definition.Key,
                description = definition.Description,
                defaultValue = definition.DefaultValue,
                allowedValues = definition.AllowedValues,
                aliases = definition.Aliases
            }),
            values = snapshot.Values
        };
    }

    [McpServerTool(Name = "prefs_delete", UseStructuredContent = true)]
    [Description("Deletes one stored preference value while preserving its configured definition.")]
    public static async Task<object> DeletePreferenceAsync(
        [Description("Configured preference key or alias.")] string key,
        CancellationToken cancellationToken = default)
    {
        var normalizedKey = await UserProfilePreferencesStore.DeleteValueAsync(key, cancellationToken);
        return new { key = normalizedKey, deleted = true };
    }

    [McpServerTool(Name = "prefs_reset_all", UseStructuredContent = true)]
    [Description("Clears all stored preference values while preserving configured definitions. Set confirm to true to perform the reset.")]
    public static async Task<object> ResetPreferencesAsync(
        [Description("Must be true to clear all stored preference values.")] bool confirm = false,
        CancellationToken cancellationToken = default)
    {
        if (!confirm)
        {
            return new { cleared = false, confirmationRequired = true };
        }

        await UserProfilePreferencesStore.ClearValuesAsync(cancellationToken);
        return new { cleared = true, confirmationRequired = false };
    }

    [McpServerTool(Name = "memory_remember", UseStructuredContent = true)]
    [Description("Stores a durable fact learned about the user and returns its identifier.")]
    public static async Task<object> RememberAsync(
        [Description("Concise durable fact to remember.")] string text,
        CancellationToken cancellationToken = default) =>
        await UserProfilePreferencesStore.RememberAsync(text, cancellationToken);

    [McpServerTool(Name = "memory_search", ReadOnly = true, UseStructuredContent = true)]
    [Description("Searches durable user facts using case-insensitive text matching.")]
    public static async Task<object> SearchMemoriesAsync(
        [Description("Text to find in remembered facts.")] string query,
        CancellationToken cancellationToken = default) =>
        new { memories = await UserProfilePreferencesStore.SearchMemoriesAsync(query, cancellationToken) };

    [McpServerTool(Name = "memory_list", ReadOnly = true, UseStructuredContent = true)]
    [Description("Lists all durable user facts.")]
    public static async Task<object> ListMemoriesAsync(CancellationToken cancellationToken = default) =>
        new { memories = await UserProfilePreferencesStore.ListMemoriesAsync(cancellationToken) };

    [McpServerTool(Name = "memory_forget", UseStructuredContent = true)]
    [Description("Forgets one durable user fact by identifier.")]
    public static async Task<object> ForgetAsync(
        [Description("Identifier returned by memory_remember or memory_list.")] string id,
        CancellationToken cancellationToken = default)
    {
        await UserProfilePreferencesStore.ForgetAsync(id, cancellationToken);
        return new { id, forgotten = true };
    }
}

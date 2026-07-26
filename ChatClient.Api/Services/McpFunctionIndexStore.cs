namespace ChatClient.Api.Services;

// MCP tool discovery is unrelated to agent knowledge and retains its own small process-local cache.
public interface IMcpFunctionIndexStore
{
    Task<IReadOnlyList<McpFunctionIndexEntry>> ReadAsync(CancellationToken cancellationToken = default);
    Task ReplaceAsync(Guid ownerId, string group, IReadOnlyList<McpFunctionIndexEntry> entries, CancellationToken cancellationToken = default);
    Task RemoveAsync(Guid ownerId, string group, CancellationToken cancellationToken = default);
    Task ClearAsync(CancellationToken cancellationToken = default);
}

public sealed record McpFunctionIndexEntry(string Group, string Name, float[] Embedding);

public sealed class McpFunctionIndexStore : IMcpFunctionIndexStore
{
    private readonly Dictionary<string, List<McpFunctionIndexEntry>> _entries = new(StringComparer.OrdinalIgnoreCase);
    public Task<IReadOnlyList<McpFunctionIndexEntry>> ReadAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<McpFunctionIndexEntry>>(_entries.Values.SelectMany(static x => x).ToList());
    public Task ReplaceAsync(Guid ownerId, string group, IReadOnlyList<McpFunctionIndexEntry> entries, CancellationToken cancellationToken = default) { _entries[group] = entries.ToList(); return Task.CompletedTask; }
    public Task RemoveAsync(Guid ownerId, string group, CancellationToken cancellationToken = default) { _entries.Remove(group); return Task.CompletedTask; }
    public Task ClearAsync(CancellationToken cancellationToken = default) { _entries.Clear(); return Task.CompletedTask; }
}

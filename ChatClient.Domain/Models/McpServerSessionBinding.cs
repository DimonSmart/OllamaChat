using System.Text.Json.Serialization;

namespace ChatClient.Domain.Models;

public sealed class McpServerSessionBinding
{
    public Guid? ServerId { get; set; }

    public string? ServerName { get; set; }

    public List<string> Roots { get; set; } = [];

    public Dictionary<string, string?> Parameters { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonIgnore]
    public bool HasIdentity =>
        (ServerId is Guid serverId && serverId != Guid.Empty) ||
        !string.IsNullOrWhiteSpace(ServerName);

    public string GetIdentityKey()
    {
        if (ServerId is Guid serverId && serverId != Guid.Empty)
        {
            return $"id:{serverId:D}";
        }

        var serverName = ServerName?.Trim();
        if (!string.IsNullOrWhiteSpace(serverName))
        {
            return $"name:{serverName}";
        }

        return string.Empty;
    }

    public bool Matches(IMcpServerDescriptor serverDescriptor)
    {
        ArgumentNullException.ThrowIfNull(serverDescriptor);

        if (ServerId is Guid serverId &&
            serverId != Guid.Empty &&
            serverDescriptor.Id is Guid descriptorId &&
            descriptorId == serverId)
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(ServerName))
        {
            return string.Equals(
                ServerName.Trim(),
                serverDescriptor.Name,
                StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    public McpServerSessionBinding Clone()
    {
        return new McpServerSessionBinding
        {
            ServerId = ServerId,
            ServerName = ServerName,
            Roots = [.. Roots],
            Parameters = new Dictionary<string, string?>(Parameters, StringComparer.OrdinalIgnoreCase)
        };
    }
}

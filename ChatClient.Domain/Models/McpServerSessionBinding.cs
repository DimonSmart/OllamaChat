using System.Text.Json.Serialization;

namespace ChatClient.Domain.Models;

public sealed class McpServerSessionBinding
{
    public Guid? BindingId { get; set; }

    public Guid? ServerId { get; set; }

    public string? ServerName { get; set; }

    public string? DisplayName { get; set; }

    public bool Enabled { get; set; } = true;

    public bool SelectAllTools { get; set; } = true;

    public List<string> SelectedTools { get; set; } = [];

    public List<string> Roots { get; set; } = [];

    public Dictionary<string, string?> Parameters { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonIgnore]
    public bool HasIdentity =>
        (ServerId is Guid serverId && serverId != Guid.Empty) ||
        !string.IsNullOrWhiteSpace(ServerName);

    [JsonIgnore]
    public bool HasBindingIdentity => BindingId is Guid bindingId && bindingId != Guid.Empty;

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

    public string GetBindingKey()
    {
        if (BindingId is Guid bindingId && bindingId != Guid.Empty)
        {
            return $"binding:{bindingId:N}";
        }

        return GetIdentityKey();
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
            var bindingServerName = ServerName.Trim();
            return string.Equals(bindingServerName, serverDescriptor.Name, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(bindingServerName, GetShortServerName(serverDescriptor.Name), StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static string GetShortServerName(string? serverName)
    {
        if (string.IsNullOrWhiteSpace(serverName))
        {
            return string.Empty;
        }

        var trimmed = serverName.Trim();
        var separatorIndex = trimmed.LastIndexOf('.');
        return separatorIndex >= 0 && separatorIndex < trimmed.Length - 1
            ? trimmed[(separatorIndex + 1)..]
            : trimmed;
    }

    public McpServerSessionBinding Clone()
    {
        return new McpServerSessionBinding
        {
            BindingId = BindingId,
            ServerId = ServerId,
            ServerName = ServerName,
            DisplayName = DisplayName,
            Enabled = Enabled,
            SelectAllTools = SelectAllTools,
            SelectedTools = [.. SelectedTools],
            Roots = [.. Roots],
            Parameters = new Dictionary<string, string?>(Parameters, StringComparer.OrdinalIgnoreCase)
        };
    }
}

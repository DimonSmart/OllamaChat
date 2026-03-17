using System.Text;
using System.Text.Json;
using ChatClient.Domain.Models;

namespace ChatClient.Api.Services;

public sealed class McpClientRequestContext
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static McpClientRequestContext Empty { get; } = new([]);

    public McpClientRequestContext(IReadOnlyCollection<McpServerSessionBinding>? bindings)
    {
        Bindings = bindings?
            .Where(static binding => binding is not null && binding.HasIdentity)
            .Select(static binding => binding.Clone())
            .OrderBy(static binding => binding.GetIdentityKey(), StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? [];
    }

    public IReadOnlyList<McpServerSessionBinding> Bindings { get; }

    public bool HasBindings => Bindings.Count > 0;

    public McpServerSessionBinding? FindBindingFor(IMcpServerDescriptor serverDescriptor)
    {
        ArgumentNullException.ThrowIfNull(serverDescriptor);

        return Bindings.FirstOrDefault(binding => binding.Matches(serverDescriptor));
    }

    public string BuildFingerprint()
    {
        if (!HasBindings)
        {
            return "no-session-bindings";
        }

        var serialized = Bindings
            .Select(binding => JsonSerializer.Serialize(binding, JsonOptions))
            .ToArray();

        return Convert.ToBase64String(Encoding.UTF8.GetBytes(string.Join("||", serialized)));
    }
}

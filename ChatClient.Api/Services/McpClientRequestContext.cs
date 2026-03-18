using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using ChatClient.Domain.Models;

namespace ChatClient.Api.Services;

public sealed class McpClientRequestContext
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static McpClientRequestContext Empty { get; } = new([]);

    public McpClientRequestContext(IReadOnlyCollection<McpServerSessionBinding>? bindings)
    {
        Bindings = bindings?
            .Where(static binding => binding is not null && binding.HasIdentity && binding.Enabled)
            .Select(static binding => binding.Clone())
            .OrderBy(static binding => binding.GetBindingKey(), StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? [];
    }

    public IReadOnlyList<McpServerSessionBinding> Bindings { get; }

    public bool HasBindings => Bindings.Count > 0;

    public McpServerSessionBinding? FindBindingFor(IMcpServerDescriptor serverDescriptor)
    {
        ArgumentNullException.ThrowIfNull(serverDescriptor);

        return Bindings.FirstOrDefault(binding => binding.Matches(serverDescriptor));
    }

    public IReadOnlyList<McpServerSessionBinding> FindBindingsFor(IMcpServerDescriptor serverDescriptor)
    {
        ArgumentNullException.ThrowIfNull(serverDescriptor);

        return Bindings
            .Where(binding => binding.Matches(serverDescriptor))
            .ToArray();
    }

    public string BuildFingerprint()
    {
        if (!HasBindings)
        {
            return "no-session-bindings";
        }

        var serialized = Bindings
            .Select(static binding => new
            {
                binding.BindingId,
                binding.ServerId,
                binding.ServerName,
                Roots = binding.Roots
                    .OrderBy(static root => root, StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                Parameters = binding.Parameters
                    .OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(static pair => new
                    {
                        pair.Key,
                        pair.Value
                    })
                    .ToArray()
            })
            .Select(item => JsonSerializer.Serialize(item, JsonOptions))
            .ToArray();

        var payload = Encoding.UTF8.GetBytes(string.Join("||", serialized));
        return Convert.ToHexString(SHA256.HashData(payload));
    }
}

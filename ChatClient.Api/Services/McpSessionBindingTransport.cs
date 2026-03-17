using System.Text;
using System.Text.Json;
using ChatClient.Domain.Models;

namespace ChatClient.Api.Services;

public static class McpSessionBindingTransport
{
    public const string ArgumentName = "--mcp-session-binding";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static string[] AppendArguments(string[]? existingArguments, McpServerSessionBinding? binding)
    {
        if (binding is null || !binding.HasIdentity)
        {
            return existingArguments ?? [];
        }

        var arguments = new List<string>(existingArguments ?? []);
        arguments.Add(ArgumentName);
        arguments.Add(Serialize(binding));
        return [.. arguments];
    }

    public static bool TryReadBinding(IReadOnlyList<string> args, out McpServerSessionBinding? binding)
    {
        for (var index = 0; index < args.Count; index++)
        {
            if (!string.Equals(args[index], ArgumentName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (index + 1 >= args.Count || string.IsNullOrWhiteSpace(args[index + 1]))
            {
                break;
            }

            binding = Deserialize(args[index + 1]);
            return binding is not null;
        }

        binding = null;
        return false;
    }

    public static string Serialize(McpServerSessionBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        var json = JsonSerializer.Serialize(binding, JsonOptions);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
    }

    public static McpServerSessionBinding? Deserialize(string base64Value)
    {
        if (string.IsNullOrWhiteSpace(base64Value))
        {
            return null;
        }

        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(base64Value));
            return JsonSerializer.Deserialize<McpServerSessionBinding>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }
}

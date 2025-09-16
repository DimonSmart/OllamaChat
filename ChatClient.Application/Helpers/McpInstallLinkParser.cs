using System.Collections.Generic;
using System.Text.Json;

using ChatClient.Domain.Models;

namespace ChatClient.Application.Helpers;

public static class McpInstallLinkParser
{
    private static readonly HashSet<string> AllowedSchemes = new(StringComparer.OrdinalIgnoreCase)
    {
        "vscode",
        "vscode-insiders"
    };

    public static McpServerConfig Parse(string link)
    {
        if (string.IsNullOrWhiteSpace(link))
            throw new ArgumentException("Link must not be empty.", nameof(link));

        if (!Uri.TryCreate(link.Trim(), UriKind.Absolute, out var uri))
            throw new InvalidOperationException("Link is not a valid URI.");

        if (!AllowedSchemes.Contains(uri.Scheme))
            throw new InvalidOperationException("Only vscode MCP install links are supported.");

        if (!uri.AbsolutePath.Equals("mcp/install", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("URI must target the mcp/install endpoint.");

        var query = uri.Query;
        if (string.IsNullOrEmpty(query) || query.Length <= 1)
            throw new InvalidOperationException("Link is missing the encoded MCP payload.");

        var payload = query[1..];
        string decodedPayload;
        try
        {
            decodedPayload = Uri.UnescapeDataString(payload);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Unable to decode MCP payload from link.", ex);
        }

        using var document = ParseJson(decodedPayload);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("MCP payload must be a JSON object.");

        var name = ReadRequiredString(root, "name");
        var type = ReadOptionalString(root, "type")?.ToLowerInvariant();

        var serverConfig = new McpServerConfig
        {
            Name = name,
            SamplingModel = ReadOptionalString(root, "samplingModel")
        };

        var command = ReadOptionalString(root, "command");
        var url = ReadOptionalString(root, "url") ?? ReadOptionalString(root, "sse");

        if (type is "stdio")
        {
            PopulateCommand(serverConfig, command, root);
        }
        else if (type is "http" or "sse")
        {
            PopulateRemote(serverConfig, url);
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(command))
            {
                PopulateCommand(serverConfig, command, root);
            }
            else if (!string.IsNullOrWhiteSpace(url))
            {
                PopulateRemote(serverConfig, url);
            }
            else
            {
                throw new InvalidOperationException(
                    "MCP payload must include either a command for local servers or a url for remote servers.");
            }
        }

        return serverConfig;
    }

    private static void PopulateCommand(McpServerConfig serverConfig, string? command, JsonElement root)
    {
        if (string.IsNullOrWhiteSpace(command))
            throw new InvalidOperationException("MCP payload must include a non-empty command.");

        serverConfig.Command = command.Trim();
        serverConfig.Sse = null;

        if (!root.TryGetProperty("args", out var argsElement) || argsElement.ValueKind == JsonValueKind.Null)
        {
            serverConfig.Arguments = null;
            return;
        }

        if (argsElement.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("MCP payload args property must be an array of strings.");

        var args = new List<string>();
        foreach (var item in argsElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                throw new InvalidOperationException("Each argument must be a string value.");

            var value = item.GetString();
            if (!string.IsNullOrWhiteSpace(value))
                args.Add(value);
        }

        serverConfig.Arguments = args.Count > 0 ? args.ToArray() : null;
    }

    private static void PopulateRemote(McpServerConfig serverConfig, string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            throw new InvalidOperationException("MCP payload must include a non-empty url for remote servers.");

        var trimmed = url.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) ||
            uri.Scheme is not "http" and not "https")
        {
            throw new InvalidOperationException("Remote MCP server url must be a valid HTTP or HTTPS URI.");
        }

        serverConfig.Sse = trimmed;
        serverConfig.Command = null;
        serverConfig.Arguments = null;
    }

    private static string? ReadOptionalString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var element))
            return null;

        if (element.ValueKind == JsonValueKind.Null)
            return null;

        if (element.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException($"Property '{propertyName}' must be a string value.");

        var value = element.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string ReadRequiredString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var element) || element.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException($"Property '{propertyName}' is required and must be a string.");

        var value = element.GetString();
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"Property '{propertyName}' must not be empty.");

        return value.Trim();
    }

    private static JsonDocument ParseJson(string payload)
    {
        try
        {
            return JsonDocument.Parse(payload);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("MCP payload contains invalid JSON.", ex);
        }
    }
}


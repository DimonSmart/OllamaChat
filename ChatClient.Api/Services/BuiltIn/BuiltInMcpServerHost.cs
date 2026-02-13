using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;

namespace ChatClient.Api.Services.BuiltIn;

public static class BuiltInMcpServerHost
{
    private const string BuiltInModeArgument = "--mcp-builtin";

    public static async Task<bool> TryRunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        if (!TryGetBuiltInKey(args, out var key))
            return false;

        await RunBuiltInServerAsync(key, cancellationToken);
        return true;
    }

    public static bool TryGetBuiltInKey(IReadOnlyList<string> args, out string key)
    {
        key = string.Empty;
        for (var i = 0; i < args.Count; i++)
        {
            if (!string.Equals(args[i], BuiltInModeArgument, StringComparison.OrdinalIgnoreCase))
                continue;

            if (i + 1 < args.Count && !string.IsNullOrWhiteSpace(args[i + 1]))
            {
                key = args[i + 1].Trim();
                return true;
            }
        }

        return false;
    }

    private static async Task RunBuiltInServerAsync(string key, CancellationToken cancellationToken)
    {
        if (!BuiltInMcpServerCatalog.TryGetDefinition(key, out var definition) || definition is null)
            throw new InvalidOperationException($"Unknown built-in MCP server key '{key}'.");

        var builder = Host.CreateApplicationBuilder([]);
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(options =>
        {
            // MCP stdio protocol uses stdout, logs should go to stderr.
            options.LogToStandardErrorThreshold = LogLevel.Trace;
        });

        var mcpBuilder = builder.Services
            .AddMcpServer(options =>
            {
                options.ServerInfo = new Implementation
                {
                    Name = definition.Name,
                    Version = "1.0.0"
                };
            })
            .WithStdioServerTransport();

        switch (definition.Key)
        {
            case BuiltInMcpServerCatalog.TimeServerKey:
                mcpBuilder.WithTools<BuiltInTimeServerTools>();
                break;
            case BuiltInMcpServerCatalog.FormattedTimeServerKey:
                mcpBuilder.WithTools<BuiltInFormattedTimeServerTools>();
                break;
            case BuiltInMcpServerCatalog.MathServerKey:
                mcpBuilder.WithTools<BuiltInMathServerTools>();
                break;
            default:
                throw new InvalidOperationException($"Unknown built-in MCP server key '{definition.Key}'.");
        }

        await builder.Build().RunAsync(cancellationToken);
    }
}

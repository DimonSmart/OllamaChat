using System.Text.Json;
using ChatClient.Api.Services;
using ChatClient.Api.Services.BuiltIn;
using ChatClient.Domain.Models;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace ChatClient.Tests;

public class BuiltInWebMcpServerIntegrationTests
{
    [Fact]
    public async Task BuiltInWebServer_AdvertisesDownloadInputSchema()
    {
        await using var fixture = new BuiltInWebMcpFixture();
        var client = await fixture.CreateClientAsync();
        var tool = (await client.ListToolsAsync())
            .First(candidate => string.Equals(candidate.Name, "download", StringComparison.Ordinal));

        var schema = tool.JsonSchema;
        Assert.Equal(JsonValueKind.Object, schema.ValueKind);
        Assert.True(schema.TryGetProperty("properties", out var properties));
        Assert.True(properties.TryGetProperty("page", out var pageSchema));
        Assert.True(properties.TryGetProperty("url", out _));
        Assert.True(pageSchema.TryGetProperty("required", out var pageRequired));
        Assert.Contains(pageRequired.EnumerateArray(), item => string.Equals(item.GetString(), "url", StringComparison.Ordinal));
        Assert.DoesNotContain(pageRequired.EnumerateArray(), item => string.Equals(item.GetString(), "title", StringComparison.Ordinal));
        Assert.True(schema.TryGetProperty("oneOf", out var oneOf));
        Assert.Equal(2, oneOf.GetArrayLength());
    }

    [Fact]
    public async Task BuiltInWebServer_AdvertisesDownloadOutputSchema()
    {
        await using var fixture = new BuiltInWebMcpFixture();
        var client = await fixture.CreateClientAsync();
        var tool = (await client.ListToolsAsync())
            .First(candidate => string.Equals(candidate.Name, "download", StringComparison.Ordinal));

        Assert.True(tool.ReturnJsonSchema.HasValue);
        var schema = tool.ReturnJsonSchema.Value;
        Assert.Equal(JsonValueKind.Object, schema.ValueKind);
        Assert.True(schema.TryGetProperty("properties", out var properties));
        Assert.True(properties.TryGetProperty("url", out _));
        Assert.True(properties.TryGetProperty("title", out _));
        Assert.True(properties.TryGetProperty("content", out _));
    }

    private sealed class BuiltInWebMcpFixture : IAsyncDisposable
    {
        private readonly ILoggerFactory _loggerFactory = LoggerFactory.Create(static _ => { });
        private McpClient? _client;

        public async Task<McpClient> CreateClientAsync()
        {
            if (_client is not null)
                return _client;

            var assemblyPath = ResolveServerAssemblyPath();
            var binding = new McpServerSessionBinding
            {
                ServerId = BuiltInWebMcpServerTools.Descriptor.Id
            };

            _client = await McpClient.CreateAsync(
                clientTransport: new StdioClientTransport(
                    new StdioClientTransportOptions
                    {
                        Name = BuiltInWebMcpServerTools.Descriptor.Name,
                        Command = "dotnet",
                        Arguments = McpSessionBindingTransport.AppendArguments(
                            [assemblyPath, "--mcp-builtin", BuiltInWebMcpServerTools.Descriptor.Key],
                            binding),
                        WorkingDirectory = Path.GetDirectoryName(assemblyPath)!
                    },
                    _loggerFactory),
                clientOptions: new McpClientOptions
                {
                    ClientInfo = new Implementation
                    {
                        Name = "ChatClient.Tests",
                        Version = "1.0.0"
                    }
                },
                loggerFactory: _loggerFactory,
                cancellationToken: CancellationToken.None);

            return _client;
        }

        public async ValueTask DisposeAsync()
        {
            if (_client is not null)
                await _client.DisposeAsync();

            _loggerFactory.Dispose();
        }

        private static string ResolveServerAssemblyPath()
        {
            var localCopy = Path.Combine(AppContext.BaseDirectory, "ChatClient.Api.dll");
            if (File.Exists(localCopy))
                return localCopy;

            var projectOutput = Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory,
                "..",
                "..",
                "..",
                "..",
                "ChatClient.Api",
                "bin",
                "Debug",
                "net10.0",
                "ChatClient.Api.dll"));

            if (File.Exists(projectOutput))
                return projectOutput;

            throw new FileNotFoundException("Unable to locate ChatClient.Api.dll for built-in MCP server integration test.");
        }
    }
}

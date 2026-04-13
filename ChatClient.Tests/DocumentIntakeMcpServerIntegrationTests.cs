using ChatClient.Api.Services;
using ChatClient.Api.Services.BuiltIn;
using ChatClient.Domain.Models;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using System.Text.Json;

namespace ChatClient.Tests;

public sealed class DocumentIntakeMcpServerIntegrationTests
{
    [Fact]
    public async Task DocumentIntakeServer_ExposesExpectedTools_AndReadsMarkdown()
    {
        await using var fixture = new DocumentIntakeMcpFixture();
        await fixture.WriteSourceAsync("resume.md", "# Candidate Resume\nBuilt APIs and workers.\n");
        var client = await fixture.CreateClientAsync();
        var tools = (await client.ListToolsAsync()).ToList();

        Assert.Contains(tools, static tool => string.Equals(tool.Name, "docintake_read_document", StringComparison.Ordinal));
        Assert.Contains(tools, static tool => string.Equals(tool.Name, "docintake_prepare_markdown", StringComparison.Ordinal));

        var toolMap = tools.ToDictionary(static tool => tool.Name, StringComparer.OrdinalIgnoreCase);

        var read = GetStructuredContent(await CallToolAsync(
            toolMap["docintake_read_document"],
            new Dictionary<string, object?>
            {
                ["sourceFile"] = fixture.ResolvePath("resume.md")
            }));
        Assert.Equal("markdown", GetProperty(read, "format").GetString());
        Assert.Equal("Candidate Resume", GetProperty(read, "title").GetString());
        Assert.Contains("Built APIs and workers", GetProperty(read, "markdown").GetString(), StringComparison.Ordinal);

        var prepared = GetStructuredContent(await CallToolAsync(
            toolMap["docintake_prepare_markdown"],
            new Dictionary<string, object?>
            {
                ["markdown"] = "## Notes\nFirst line.\nSecond line.",
                ["fallbackTitle"] = "Fallback"
            }));
        Assert.Equal("Notes", GetProperty(prepared, "title").GetString());
        Assert.True(GetProperty(prepared, "lineCount").GetInt32() >= 2);
        Assert.True(GetProperty(prepared, "wordCount").GetInt32() >= 3);
    }

    [Fact]
    public async Task DocumentIntakeServer_ReturnsHelpfulError_ForUnsupportedFormat()
    {
        await using var fixture = new DocumentIntakeMcpFixture();
        await fixture.WriteSourceAsync("resume.pdf", "not-a-real-pdf");
        var client = await fixture.CreateClientAsync();
        var tool = (await client.ListToolsAsync())
            .First(static candidate => string.Equals(candidate.Name, "docintake_read_document", StringComparison.Ordinal));

        var result = await CallToolAsync(
            tool,
            new Dictionary<string, object?>
            {
                ["sourceFile"] = fixture.ResolvePath("resume.pdf")
            });

        Assert.True(GetBooleanProperty(result, "isError"));
        var details = GetStructuredContent(result);
        Assert.Equal("unsupported_source_format", GetProperty(details, "code").GetString());
    }

    private static async Task<JsonElement> CallToolAsync(McpClientTool tool, Dictionary<string, object?> arguments)
    {
        var result = await tool.CallAsync(arguments, null, null);
        return JsonSerializer.SerializeToElement(result);
    }

    private static JsonElement GetStructuredContent(JsonElement toolResult)
    {
        if (TryGetProperty(toolResult, "structuredContent", out var structuredContent))
        {
            return TryGetProperty(structuredContent, "result", out var payload)
                ? payload
                : structuredContent;
        }

        throw new Xunit.Sdk.XunitException($"Tool result does not contain structuredContent: {toolResult}");
    }

    private static JsonElement GetProperty(JsonElement element, string propertyName)
    {
        if (TryGetProperty(element, propertyName, out var value))
        {
            return value;
        }

        throw new Xunit.Sdk.XunitException($"Property '{propertyName}' was not found in {element}");
    }

    private static bool GetBooleanProperty(JsonElement element, string propertyName)
    {
        return TryGetProperty(element, propertyName, out var value) &&
               value.ValueKind is JsonValueKind.True or JsonValueKind.False &&
               value.GetBoolean();
    }

    private static bool TryGetProperty(JsonElement element, string propertyName, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(propertyName, out value))
        {
            return true;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private sealed class DocumentIntakeMcpFixture : IAsyncDisposable
    {
        private readonly DirectoryInfo _root = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "document-intake-mcp", Guid.NewGuid().ToString("N")));
        private readonly ILoggerFactory _loggerFactory = LoggerFactory.Create(static builder => builder.AddDebug().SetMinimumLevel(LogLevel.Debug));
        private McpClient? _client;

        public string ResolvePath(string fileName) => Path.Combine(_root.FullName, fileName);

        public async Task WriteSourceAsync(string fileName, string content)
        {
            await File.WriteAllTextAsync(ResolvePath(fileName), content);
        }

        public async Task<McpClient> CreateClientAsync()
        {
            if (_client is not null)
            {
                return _client;
            }

            var assemblyPath = ResolveServerAssemblyPath();
            _client = await McpClient.CreateAsync(
                clientTransport: new StdioClientTransport(
                    new StdioClientTransportOptions
                    {
                        Name = BuiltInDocumentIntakeMcpServerTools.Descriptor.Name,
                        Command = "dotnet",
                        Arguments = [assemblyPath, "--mcp-builtin", BuiltInDocumentIntakeMcpServerTools.Descriptor.Key],
                        WorkingDirectory = Path.GetDirectoryName(assemblyPath)!
                    },
                    _loggerFactory),
                clientOptions: new McpClientOptions
                {
                    ClientInfo = new Implementation
                    {
                        Name = "DocumentIntakeMcpServerIntegrationTests",
                        Version = "1.0.0"
                    }
                });

            return _client;
        }

        public async ValueTask DisposeAsync()
        {
            if (_client is not null)
            {
                await _client.DisposeAsync();
            }

            try
            {
                if (_root.Exists)
                {
                    _root.Delete(recursive: true);
                }
            }
            catch
            {
            }

            _loggerFactory.Dispose();
        }

        private static string ResolveServerAssemblyPath()
        {
            var localCopy = Path.Combine(AppContext.BaseDirectory, "ChatClient.Api.dll");
            if (File.Exists(localCopy))
            {
                return localCopy;
            }

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
            {
                return projectOutput;
            }

            throw new FileNotFoundException("Unable to locate ChatClient.Api.dll for built-in MCP server integration test.");
        }
    }
}

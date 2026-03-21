using System.Text.Json;
using ChatClient.Api.Client.Pages;
using ChatClient.Api.Services;
using ChatClient.Api.Services.BuiltIn;
using ChatClient.Domain.Models;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace ChatClient.Tests;

public class BuiltInMarkdownDocumentMcpServerIntegrationTests
{
    [Fact]
    public async Task MarkdownDocumentServer_ExposesExpectedTools_NavigatesAndEditsMarkdown()
    {
        await using var fixture = new MarkdownDocumentMcpFixture();
        await fixture.WriteSourceAsync(
            """
            Preface paragraph.

            # Chapter One

            Intro for chapter one.

            ## Alice

            Alice is curious and brave.

            ## Bob

            Bob likes machines.

            # Chapter Two

            Another chapter.
            """);
        var client = await fixture.CreateClientAsync();
        var tools = (await client.ListToolsAsync()).ToList();

        Assert.Contains(tools, static tool => string.Equals(tool.Name, "doc_get_context", StringComparison.Ordinal));
        Assert.Contains(tools, static tool => string.Equals(tool.Name, "doc_list_headings", StringComparison.Ordinal));
        Assert.Contains(tools, static tool => string.Equals(tool.Name, "doc_get_section", StringComparison.Ordinal));
        Assert.Contains(tools, static tool => string.Equals(tool.Name, "doc_list_items", StringComparison.Ordinal));
        Assert.Contains(tools, static tool => string.Equals(tool.Name, "doc_search_sections", StringComparison.Ordinal));
        Assert.Contains(tools, static tool => string.Equals(tool.Name, "doc_apply_operations", StringComparison.Ordinal));
        Assert.Contains(tools, static tool => string.Equals(tool.Name, "doc_export_markdown", StringComparison.Ordinal));

        var toolMap = tools.ToDictionary(static tool => tool.Name, StringComparer.OrdinalIgnoreCase);

        var context = GetStructuredContent(await CallToolAsync(toolMap["doc_get_context"], new Dictionary<string, object?>()));
        Assert.Equal(Path.GetFullPath(fixture.SourceFilePath), GetProperty(context, "sourceFile").GetString());
        Assert.True(GetProperty(context, "itemCount").GetInt32() >= 7);

        var listHeadings = GetStructuredContent(await CallToolAsync(
            toolMap["doc_list_headings"],
            new Dictionary<string, object?>
            {
                ["outline"] = "0",
                ["maxDepth"] = 3
            }));
        var headings = GetProperty(listHeadings, "headings").EnumerateArray().ToArray();
        Assert.Equal(4, headings.Length);
        Assert.Equal("1", GetProperty(headings[0], "outline").GetString());
        Assert.Equal("1.1", GetProperty(headings[1], "outline").GetString());
        Assert.Equal("1.2", GetProperty(headings[2], "outline").GetString());
        Assert.Equal("2", GetProperty(headings[3], "outline").GetString());

        var aliceSection = GetStructuredContent(await CallToolAsync(
            toolMap["doc_get_section"],
            new Dictionary<string, object?>
            {
                ["outline"] = "1.1"
            }));
        Assert.Equal("Alice", GetProperty(aliceSection, "title").GetString());
        Assert.Contains(
            "curious and brave",
            GetProperty(aliceSection, "contentMarkdown").GetString(),
            StringComparison.OrdinalIgnoreCase);

        var itemBatch = GetStructuredContent(await CallToolAsync(
            toolMap["doc_list_items"],
            new Dictionary<string, object?>
            {
                ["outline"] = "1",
                ["maxItems"] = 10,
                ["includeHeadings"] = false
            }));
        var items = GetProperty(itemBatch, "items").EnumerateArray().ToArray();
        Assert.Equal(3, items.Length);
        Assert.Equal("1.p1", GetProperty(items[0], "pointer").GetString());
        Assert.Equal("1.1.p1", GetProperty(items[1], "pointer").GetString());
        Assert.Equal("1.2.p1", GetProperty(items[2], "pointer").GetString());

        var search = GetStructuredContent(await CallToolAsync(
            toolMap["doc_search_sections"],
            new Dictionary<string, object?>
            {
                ["query"] = "machines",
                ["maxResults"] = 5
            }));
        var hits = GetProperty(search, "hits").EnumerateArray().ToArray();
        var firstHit = Assert.Single(hits);
        Assert.Equal("Bob", GetProperty(firstHit, "title").GetString());
        Assert.Equal("1.2", GetProperty(firstHit, "outline").GetString());

        var applyResult = GetStructuredContent(await CallToolAsync(
            toolMap["doc_apply_operations"],
            new Dictionary<string, object?>
            {
                ["operations"] = new object[]
                {
                    new Dictionary<string, object?>
                    {
                        ["action"] = "replace",
                        ["targetPointer"] = "1.2.p1",
                        ["items"] = new object[]
                        {
                            new Dictionary<string, object?>
                            {
                                ["markdown"] = "Bob builds rockets."
                            }
                        }
                    }
                }
            }));
        Assert.Equal(1, GetProperty(applyResult, "appliedOperationCount").GetInt32());

        var markdownExport = GetTextContent(await CallToolAsync(
            toolMap["doc_export_markdown"],
            new Dictionary<string, object?>()));
        Assert.Contains("Bob builds rockets.", markdownExport, StringComparison.Ordinal);
        Assert.DoesNotContain("Bob likes machines.", markdownExport, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MarkdownDocumentListItemsSchema_IsConsumableByPlaygroundHelper()
    {
        await using var fixture = new MarkdownDocumentMcpFixture();
        await fixture.WriteSourceAsync("# Only Heading");
        var client = await fixture.CreateClientAsync();
        var tool = (await client.ListToolsAsync())
            .First(static candidate => string.Equals(candidate.Name, "doc_list_items", StringComparison.Ordinal));

        var fields = McpPlaygroundToolFormHelper.CreateFields(tool.JsonSchema);

        Assert.Contains(fields, static field => string.Equals(field.Name, "outline", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(fields, static field => string.Equals(field.Name, "maxItems", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(fields, static field => string.Equals(field.Name, "includeHeadings", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task MarkdownDocumentApplyOperations_ReturnsHelpfulError_ForInvalidPointer()
    {
        await using var fixture = new MarkdownDocumentMcpFixture();
        await fixture.WriteSourceAsync("# Chapter");
        var client = await fixture.CreateClientAsync();
        var tool = (await client.ListToolsAsync())
            .First(static candidate => string.Equals(candidate.Name, "doc_apply_operations", StringComparison.Ordinal));

        var result = await CallToolAsync(
            tool,
            new Dictionary<string, object?>
            {
                ["operations"] = new object[]
                {
                    new Dictionary<string, object?>
                    {
                        ["action"] = "remove",
                        ["targetPointer"] = "bad-pointer"
                    }
                }
            });

        Assert.True(GetBooleanProperty(result, "isError"));
        Assert.Contains(
            "Invalid semantic pointer",
            GetTextContent(result),
            StringComparison.OrdinalIgnoreCase);

        var details = GetStructuredContent(result);
        Assert.Equal("invalid_pointer", GetProperty(details, "code").GetString());
    }

    private static async Task<JsonElement> CallToolAsync(
        McpClientTool tool,
        Dictionary<string, object?> arguments)
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

    private static string GetTextContent(JsonElement toolResult)
    {
        if (!TryGetProperty(toolResult, "content", out var content) ||
            content.ValueKind != JsonValueKind.Array)
        {
            throw new Xunit.Sdk.XunitException($"Tool result does not contain content: {toolResult}");
        }

        return string.Join(
            Environment.NewLine,
            content.EnumerateArray()
                .Where(static item => item.ValueKind == JsonValueKind.Object)
                .Select(static item =>
                    TryGetProperty(item, "text", out var textNode) && textNode.ValueKind == JsonValueKind.String
                        ? textNode.GetString()
                        : null)
                .Where(static text => !string.IsNullOrWhiteSpace(text))!);
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

    private sealed class MarkdownDocumentMcpFixture : IAsyncDisposable
    {
        private readonly DirectoryInfo _root = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "markdown-document-mcp", Guid.NewGuid().ToString("N")));
        private readonly ILoggerFactory _loggerFactory = LoggerFactory.Create(static builder => builder.AddDebug().SetMinimumLevel(LogLevel.Debug));
        private McpClient? _client;

        public string SourceFilePath => Path.Combine(_root.FullName, "book.md");

        public async Task WriteSourceAsync(string markdown)
        {
            await File.WriteAllTextAsync(SourceFilePath, markdown);
        }

        public async Task<McpClient> CreateClientAsync()
        {
            if (_client is not null)
            {
                return _client;
            }

            var assemblyPath = ResolveServerAssemblyPath();
            var binding = new McpServerSessionBinding
            {
                ServerId = BuiltInMarkdownDocumentMcpServerTools.Descriptor.Id,
                Parameters = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                {
                    [MarkdownDocumentSession.SourceFileParameter] = SourceFilePath
                }
            };

            _client = await McpClient.CreateAsync(
                clientTransport: new StdioClientTransport(
                    new StdioClientTransportOptions
                    {
                        Name = BuiltInMarkdownDocumentMcpServerTools.Descriptor.Name,
                        Command = "dotnet",
                        Arguments = McpSessionBindingTransport.AppendArguments(
                            [assemblyPath, "--mcp-builtin", BuiltInMarkdownDocumentMcpServerTools.Descriptor.Key],
                            binding),
                        WorkingDirectory = Path.GetDirectoryName(assemblyPath)!
                    },
                    _loggerFactory),
                clientOptions: new McpClientOptions
                {
                    ClientInfo = new Implementation
                    {
                        Name = "BuiltInMarkdownDocumentMcpServerIntegrationTests",
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
            await Task.CompletedTask;
        }

        private static string ResolveServerAssemblyPath()
        {
            var testAssemblyDirectory = AppContext.BaseDirectory;
            var candidatePaths = new[]
            {
                Path.GetFullPath(Path.Combine(testAssemblyDirectory, "ChatClient.Api.dll")),
                Path.GetFullPath(Path.Combine(testAssemblyDirectory, "..", "..", "..", "..", "ChatClient.Api", "bin", "Debug", "net10.0", "ChatClient.Api.dll"))
            };

            foreach (var candidate in candidatePaths)
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            throw new FileNotFoundException("Unable to locate ChatClient.Api.dll for built-in MCP server integration test.");
        }
    }
}

using System.Text.Json;
using ChatClient.Api.Client.Pages;
using ChatClient.Api.Services;
using ChatClient.Api.Services.BuiltIn;
using ChatClient.Domain.Models;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace ChatClient.Tests;

public class KnowledgeBookMcpServerIntegrationTests
{
    [Fact]
    public async Task KnowledgeBookServer_ExposesExpectedTools_AndExecutesAllMethods()
    {
        await using var fixture = new KnowledgeBookMcpFixture();
        var client = await fixture.CreateClientAsync();
        var tools = (await client.ListToolsAsync()).ToList();

        Assert.Contains(tools, static tool => string.Equals(tool.Name, "kb_get_context", StringComparison.Ordinal));
        Assert.Contains(tools, static tool => string.Equals(tool.Name, "kb_list_headings", StringComparison.Ordinal));
        Assert.Contains(tools, static tool => string.Equals(tool.Name, "kb_get_section", StringComparison.Ordinal));
        Assert.Contains(tools, static tool => string.Equals(tool.Name, "kb_insert_section", StringComparison.Ordinal));
        Assert.Contains(tools, static tool => string.Equals(tool.Name, "kb_update_section", StringComparison.Ordinal));
        Assert.Contains(tools, static tool => string.Equals(tool.Name, "kb_search_sections", StringComparison.Ordinal));
        Assert.Contains(tools, static tool => string.Equals(tool.Name, "kb_export_markdown", StringComparison.Ordinal));

        var toolMap = tools.ToDictionary(static tool => tool.Name, StringComparer.OrdinalIgnoreCase);

        var context = GetStructuredContent(await CallToolAsync(
            toolMap["kb_get_context"],
            new Dictionary<string, object?>()));
        Assert.Equal(Path.GetFullPath(fixture.KnowledgeFilePath), GetProperty(context, "knowledgeFile").GetString());

        var listBeforeInsert = GetStructuredContent(await CallToolAsync(
            toolMap["kb_list_headings"],
            new Dictionary<string, object?>()));
        Assert.Equal(JsonValueKind.Array, GetProperty(listBeforeInsert, "headings").ValueKind);
        Assert.Empty(GetProperty(listBeforeInsert, "headings").EnumerateArray());

        var rootSection = GetStructuredContent(await CallToolAsync(
            toolMap["kb_get_section"],
            new Dictionary<string, object?>()));
        Assert.Equal("Knowledge Book", GetProperty(rootSection, "title").GetString());
        Assert.Equal("0", GetProperty(rootSection, "outline").GetString());

        var cookingResult = GetStructuredContent(await CallToolAsync(
            toolMap["kb_insert_section"],
            new Dictionary<string, object?>
            {
                ["title"] = "Cooking"
            }));
        Assert.Equal("Cooking", GetProperty(cookingResult, "title").GetString());
        Assert.Equal("1", GetProperty(cookingResult, "outline").GetString());

        var pastryResult = GetStructuredContent(await CallToolAsync(
            toolMap["kb_insert_section"],
            new Dictionary<string, object?>
            {
                ["title"] = "Pastry",
                ["anchorOutline"] = "1",
                ["asChild"] = true
            }));
        Assert.Equal("Pastry", GetProperty(pastryResult, "title").GetString());
        Assert.Equal("1.1", GetProperty(pastryResult, "outline").GetString());

        var eclairsInsertResult = GetStructuredContent(await CallToolAsync(
            toolMap["kb_insert_section"],
            new Dictionary<string, object?>
            {
                ["title"] = "Eclairs",
                ["anchorOutline"] = "1.1",
                ["asChild"] = true
            }));
        Assert.Equal("Eclairs", GetProperty(eclairsInsertResult, "title").GetString());
        Assert.Equal("1.1.1", GetProperty(eclairsInsertResult, "outline").GetString());

        var updateResult = GetStructuredContent(await CallToolAsync(
            toolMap["kb_update_section"],
            new Dictionary<string, object?>
            {
                ["outline"] = "1.1.1",
                ["contentMarkdown"] = "Use choux pastry and pastry cream."
            }));
        Assert.Equal("Eclairs", GetProperty(updateResult, "title").GetString());
        Assert.Equal("1.1.1", GetProperty(updateResult, "outline").GetString());

        var listAfterInsert = GetStructuredContent(await CallToolAsync(
            toolMap["kb_list_headings"],
            new Dictionary<string, object?>
            {
                ["outline"] = "0",
                ["maxDepth"] = 3
            }));
        Assert.Equal(3, GetProperty(listAfterInsert, "headings").GetArrayLength());

        var nestedSection = GetStructuredContent(await CallToolAsync(
            toolMap["kb_get_section"],
            new Dictionary<string, object?>
            {
                ["outline"] = "1.1.1"
            }));
        Assert.Equal("Eclairs", GetProperty(nestedSection, "title").GetString());
        Assert.Contains(
            "choux pastry",
            GetProperty(nestedSection, "contentMarkdown").GetString(),
            StringComparison.OrdinalIgnoreCase);

        var searchResult = GetStructuredContent(await CallToolAsync(
            toolMap["kb_search_sections"],
            new Dictionary<string, object?>
            {
                ["query"] = "choux",
                ["maxResults"] = 5
            }));
        var hits = GetProperty(searchResult, "hits").EnumerateArray().ToArray();
        var firstHit = Assert.Single(hits);
        Assert.Equal("Eclairs", GetProperty(firstHit, "title").GetString());
        Assert.Equal("1.1.1", GetProperty(firstHit, "outline").GetString());

        var markdownExport = GetTextContent(await CallToolAsync(
            toolMap["kb_export_markdown"],
            new Dictionary<string, object?>()));
        Assert.Contains("# Cooking", markdownExport, StringComparison.Ordinal);
        Assert.Contains("## Pastry", markdownExport, StringComparison.Ordinal);
        Assert.Contains("### Eclairs", markdownExport, StringComparison.Ordinal);
        Assert.Contains("Use choux pastry and pastry cream.", markdownExport, StringComparison.Ordinal);
    }

    [Fact]
    public async Task KnowledgeBookListHeadingsSchema_IsConsumableByPlaygroundHelper()
    {
        await using var fixture = new KnowledgeBookMcpFixture();
        var client = await fixture.CreateClientAsync();
        var tool = (await client.ListToolsAsync())
            .First(static candidate => string.Equals(candidate.Name, "kb_list_headings", StringComparison.Ordinal));

        var fields = McpPlaygroundToolFormHelper.CreateFields(tool.JsonSchema);

        Assert.Contains(fields, static field => string.Equals(field.Name, "outline", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(fields, static field => string.Equals(field.Name, "maxDepth", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task KnowledgeBookInsertSection_ReturnsHelpfulError_ForEmptyTitle()
    {
        await using var fixture = new KnowledgeBookMcpFixture();
        var client = await fixture.CreateClientAsync();
        var tool = (await client.ListToolsAsync())
            .First(static candidate => string.Equals(candidate.Name, "kb_insert_section", StringComparison.Ordinal));

        var result = await CallToolAsync(
            tool,
            new Dictionary<string, object?>
            {
                ["title"] = "   "
            });

        Assert.True(GetBooleanProperty(result, "isError"));
        Assert.Contains(
            "Provide a non-empty title",
            GetTextContent(result),
            StringComparison.OrdinalIgnoreCase);

        var details = GetStructuredContent(result);
        Assert.Equal("title_required", GetProperty(details, "code").GetString());
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
        var value = GetProperty(element, propertyName);
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new Xunit.Sdk.XunitException($"Property '{propertyName}' is not a boolean in {element}")
        };
    }

    private static bool TryGetProperty(JsonElement element, string propertyName, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private sealed class KnowledgeBookMcpFixture : IAsyncDisposable
    {
        private readonly DirectoryInfo _root = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "knowledge-book-mcp", Guid.NewGuid().ToString("N")));
        private readonly ILoggerFactory _loggerFactory = LoggerFactory.Create(static _ => { });
        private McpClient? _client;

        public string KnowledgeFilePath => Path.Combine(_root.FullName, "knowledge-book.json");

        public async Task<McpClient> CreateClientAsync()
        {
            if (_client is not null)
            {
                return _client;
            }

            var assemblyPath = ResolveServerAssemblyPath();
            var binding = new McpServerSessionBinding
            {
                ServerId = BuiltInKnowledgeBookMcpServerTools.Descriptor.Id,
                Parameters = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                {
                    [KnowledgeBookStore.KnowledgeFileParameter] = KnowledgeFilePath
                }
            };

            _client = await McpClient.CreateAsync(
                clientTransport: new StdioClientTransport(
                    new StdioClientTransportOptions
                    {
                        Name = BuiltInKnowledgeBookMcpServerTools.Descriptor.Name,
                        Command = "dotnet",
                        Arguments = McpSessionBindingTransport.AppendArguments(
                            [assemblyPath, "--mcp-builtin", BuiltInKnowledgeBookMcpServerTools.Descriptor.Key],
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
            {
                await _client.DisposeAsync();
            }

            _loggerFactory.Dispose();

            if (_root.Exists)
            {
                _root.Delete(recursive: true);
            }
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

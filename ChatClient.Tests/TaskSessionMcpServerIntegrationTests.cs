using System.Text.Json;
using ChatClient.Api.Services;
using ChatClient.Api.Services.BuiltIn;
using ChatClient.Domain.Models;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace ChatClient.Tests;

public sealed class TaskSessionMcpServerIntegrationTests
{
    [Fact]
    public async Task TaskSessionServer_ExposesExpectedTools_AndRoundTripsState()
    {
        await using var fixture = new TaskSessionMcpFixture();
        var client = await fixture.CreateClientAsync();
        var tools = (await client.ListToolsAsync()).ToList();

        Assert.Contains(tools, static tool => string.Equals(tool.Name, "session_get_context", StringComparison.Ordinal));
        Assert.Contains(tools, static tool => string.Equals(tool.Name, "session_create", StringComparison.Ordinal));
        Assert.Contains(tools, static tool => string.Equals(tool.Name, "session_get", StringComparison.Ordinal));
        Assert.Contains(tools, static tool => string.Equals(tool.Name, "session_set_phase", StringComparison.Ordinal));
        Assert.Contains(tools, static tool => string.Equals(tool.Name, "session_attach_document", StringComparison.Ordinal));
        Assert.Contains(tools, static tool => string.Equals(tool.Name, "session_get_document", StringComparison.Ordinal));
        Assert.Contains(tools, static tool => string.Equals(tool.Name, "session_append_turn", StringComparison.Ordinal));
        Assert.Contains(tools, static tool => string.Equals(tool.Name, "session_list_turns", StringComparison.Ordinal));
        Assert.Contains(tools, static tool => string.Equals(tool.Name, "session_save_summary", StringComparison.Ordinal));

        var toolMap = tools.ToDictionary(static tool => tool.Name, StringComparer.OrdinalIgnoreCase);

        var context = GetStructuredContent(await CallToolAsync(toolMap["session_get_context"], []));
        Assert.Equal(Path.GetFullPath(fixture.DatabaseFilePath), GetProperty(context, "databaseFile").GetString());

        var created = GetStructuredContent(await CallToolAsync(
            toolMap["session_create"],
            new Dictionary<string, object?>
            {
                ["title"] = "Interview Prep",
                ["description"] = "Prepare for backend interview."
            }));
        var sessionId = GetProperty(created, "sessionId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(sessionId));

        var phaseUpdated = GetStructuredContent(await CallToolAsync(
            toolMap["session_set_phase"],
            new Dictionary<string, object?>
            {
                ["sessionId"] = sessionId,
                ["phase"] = "behavioural"
            }));
        Assert.Equal("behavioural", GetProperty(phaseUpdated, "phase").GetString());

        var attached = GetStructuredContent(await CallToolAsync(
            toolMap["session_attach_document"],
            new Dictionary<string, object?>
            {
                ["sessionId"] = sessionId,
                ["kind"] = "resume",
                ["title"] = "Resume",
                ["markdown"] = "# Resume\nBackend engineer.",
                ["source"] = "resume.md"
            }));
        Assert.Equal("resume", GetProperty(attached, "kind").GetString());

        var document = GetStructuredContent(await CallToolAsync(
            toolMap["session_get_document"],
            new Dictionary<string, object?>
            {
                ["sessionId"] = sessionId,
                ["kind"] = "resume"
            }));
        Assert.Contains("Backend engineer", GetProperty(document, "markdown").GetString(), StringComparison.Ordinal);

        await CallToolAsync(
            toolMap["session_append_turn"],
            new Dictionary<string, object?>
            {
                ["sessionId"] = sessionId,
                ["role"] = "user",
                ["speakerId"] = "user",
                ["content"] = "I want to practice."
            });
        await CallToolAsync(
            toolMap["session_append_turn"],
            new Dictionary<string, object?>
            {
                ["sessionId"] = sessionId,
                ["role"] = "assistant",
                ["speakerId"] = "behavioural",
                ["content"] = "Tell me about a time you handled conflict."
            });

        var turns = GetStructuredContent(await CallToolAsync(
            toolMap["session_list_turns"],
            new Dictionary<string, object?>
            {
                ["sessionId"] = sessionId,
                ["afterSequence"] = 0,
                ["maxCount"] = 10
            }));
        Assert.Equal(2, GetProperty(turns, "turns").GetArrayLength());

        var summary = GetStructuredContent(await CallToolAsync(
            toolMap["session_save_summary"],
            new Dictionary<string, object?>
            {
                ["sessionId"] = sessionId,
                ["label"] = "final",
                ["markdown"] = "Strong behavioural answers."
            }));
        Assert.Equal("final", GetProperty(summary, "label").GetString());

        var snapshot = GetStructuredContent(await CallToolAsync(
            toolMap["session_get"],
            new Dictionary<string, object?>
            {
                ["sessionId"] = sessionId
            }));
        Assert.Equal("behavioural", GetProperty(snapshot, "phase").GetString());
        Assert.Equal(2, GetProperty(snapshot, "turnCount").GetInt32());
        Assert.Single(GetProperty(snapshot, "documents").EnumerateArray());
        Assert.Single(GetProperty(snapshot, "summaries").EnumerateArray());
    }

    [Fact]
    public async Task TaskSessionServer_UsesBoundSessionId_WhenToolArgumentIsOmitted()
    {
        await using var fixture = new TaskSessionMcpFixture();
        var initialClient = await fixture.CreateClientAsync();
        var initialToolMap = (await initialClient.ListToolsAsync())
            .ToDictionary(static tool => tool.Name, StringComparer.OrdinalIgnoreCase);

        var created = GetStructuredContent(await CallToolAsync(
            initialToolMap["session_create"],
            new Dictionary<string, object?>
            {
                ["title"] = "Bound Session"
            }));
        var sessionId = GetProperty(created, "sessionId").GetString();

        var boundClient = await fixture.CreateClientAsync(sessionId);
        var toolMap = (await boundClient.ListToolsAsync())
            .ToDictionary(static tool => tool.Name, StringComparer.OrdinalIgnoreCase);

        var phaseUpdated = GetStructuredContent(await CallToolAsync(
            toolMap["session_set_phase"],
            new Dictionary<string, object?>
            {
                ["phase"] = "technical"
            }));
        Assert.Equal("technical", GetProperty(phaseUpdated, "phase").GetString());

        await CallToolAsync(
            toolMap["session_attach_document"],
            new Dictionary<string, object?>
            {
                ["kind"] = "job_description",
                ["markdown"] = "# JD\nBuild distributed systems."
            });

        var snapshot = GetStructuredContent(await CallToolAsync(toolMap["session_get"], []));
        Assert.Equal(sessionId, GetProperty(snapshot, "sessionId").GetString());
        Assert.Equal("technical", GetProperty(snapshot, "phase").GetString());
        Assert.Single(GetProperty(snapshot, "documents").EnumerateArray());
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

    private sealed class TaskSessionMcpFixture : IAsyncDisposable
    {
        private readonly DirectoryInfo _root = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "task-session-mcp", Guid.NewGuid().ToString("N")));
        private readonly ILoggerFactory _loggerFactory = LoggerFactory.Create(static builder => builder.AddDebug().SetMinimumLevel(LogLevel.Debug));
        private readonly List<McpClient> _clients = [];

        public string DatabaseFilePath => Path.Combine(_root.FullName, "task-sessions.db");

        public async Task<McpClient> CreateClientAsync(string? sessionId = null)
        {
            var assemblyPath = ResolveServerAssemblyPath();
            var binding = new McpServerSessionBinding
            {
                ServerId = BuiltInTaskSessionMcpServerTools.Descriptor.Id,
                Parameters = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                {
                    [TaskSessionStore.DatabaseFileParameter] = DatabaseFilePath,
                    [TaskSessionStore.SessionIdParameter] = sessionId
                }
            };

            var client = await McpClient.CreateAsync(
                clientTransport: new StdioClientTransport(
                    new StdioClientTransportOptions
                    {
                        Name = BuiltInTaskSessionMcpServerTools.Descriptor.Name,
                        Command = "dotnet",
                        Arguments = McpSessionBindingTransport.AppendArguments(
                            [assemblyPath, "--mcp-builtin", BuiltInTaskSessionMcpServerTools.Descriptor.Key],
                            binding),
                        WorkingDirectory = Path.GetDirectoryName(assemblyPath)!
                    },
                    _loggerFactory),
                clientOptions: new McpClientOptions
                {
                    ClientInfo = new Implementation
                    {
                        Name = "TaskSessionMcpServerIntegrationTests",
                        Version = "1.0.0"
                    }
                });

            _clients.Add(client);
            return client;
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var client in _clients)
            {
                await client.DisposeAsync();
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

using ChatClient.Api.Services;
using ChatClient.Api.Services.BuiltIn;
using ChatClient.Domain.Models;
using ChatClient.Infrastructure.Services.TaskSessions;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using System.Text.Json;

namespace ChatClient.Tests;

public sealed class TaskSessionMcpServerIntegrationTests
{
    [Fact]
    public async Task TaskSessionServer_ExposesExpectedTools_AndRoundTripsState()
    {
        await using var fixture = new TaskSessionMcpFixture();
        var sessionId = await fixture.InitializeWorkflowRunAsync();
        var client = await fixture.CreateClientAsync(sessionId);
        var tools = (await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken)).ToList();

        Assert.DoesNotContain(tools, static tool => string.Equals(tool.Name, "session_get_context", StringComparison.Ordinal));
        Assert.DoesNotContain(tools, static tool => string.Equals(tool.Name, "session_create", StringComparison.Ordinal));
        Assert.Contains(tools, static tool => string.Equals(tool.Name, "session_get", StringComparison.Ordinal));
        Assert.Contains(tools, static tool => string.Equals(tool.Name, "session_set_phase", StringComparison.Ordinal));
        Assert.DoesNotContain(tools, static tool => string.Equals(tool.Name, "session_attach_document", StringComparison.Ordinal));
        Assert.Contains(tools, static tool => string.Equals(tool.Name, "session_get_document", StringComparison.Ordinal));
        Assert.DoesNotContain(tools, static tool => string.Equals(tool.Name, "session_set_parameter", StringComparison.Ordinal));
        Assert.Contains(tools, static tool => string.Equals(tool.Name, "session_get_parameter", StringComparison.Ordinal));
        Assert.DoesNotContain(tools, static tool => string.Equals(tool.Name, "session_append_turn", StringComparison.Ordinal));
        Assert.DoesNotContain(tools, static tool => string.Equals(tool.Name, "session_list_turns", StringComparison.Ordinal));
        Assert.Contains(tools, static tool => string.Equals(tool.Name, "session_save_summary", StringComparison.Ordinal));

        var toolMap = tools.ToDictionary(static tool => tool.Name, StringComparer.OrdinalIgnoreCase);

        var phaseUpdated = GetStructuredContent(await CallToolAsync(
            toolMap["session_set_phase"],
            new Dictionary<string, object?>
            {
                ["sessionId"] = sessionId,
                ["phase"] = "behavioural"
            }));
        Assert.Equal("behavioural", GetProperty(phaseUpdated, "phase").GetString());

        var document = GetStructuredContent(await CallToolAsync(
            toolMap["session_get_document"],
            new Dictionary<string, object?>
            {
                ["sessionId"] = sessionId,
                ["kind"] = "resume"
            }));
        Assert.Contains("Backend engineer", GetProperty(document, "markdown").GetString(), StringComparison.Ordinal);

        var loadedParameter = GetStructuredContent(await CallToolAsync(
            toolMap["session_get_parameter"],
            new Dictionary<string, object?>
            {
                ["sessionId"] = sessionId,
                ["key"] = "response_language"
            }));
        Assert.Equal("English", GetProperty(loadedParameter, "value").GetString());

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
        Assert.False(snapshot.TryGetProperty("turnCount", out _));
        Assert.Single(GetProperty(snapshot, "documents").EnumerateArray());
        Assert.Single(GetProperty(snapshot, "parameters").EnumerateArray());
        Assert.Single(GetProperty(snapshot, "summaries").EnumerateArray());
    }

    [Fact]
    public async Task TaskSessionServer_UsesBoundSessionId_WhenToolArgumentIsOmitted()
    {
        await using var fixture = new TaskSessionMcpFixture();
        var sessionId = await fixture.InitializeWorkflowRunAsync();

        var boundClient = await fixture.CreateClientAsync(sessionId);
        var toolMap = (await boundClient.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken))
            .ToDictionary(static tool => tool.Name, StringComparer.OrdinalIgnoreCase);

        var phaseUpdated = GetStructuredContent(await CallToolAsync(
            toolMap["session_set_phase"],
            new Dictionary<string, object?>
            {
                ["phase"] = "technical"
            }));
        Assert.Equal("technical", GetProperty(phaseUpdated, "phase").GetString());

        var snapshot = GetStructuredContent(await CallToolAsync(toolMap["session_get"], []));
        Assert.Equal(sessionId, GetProperty(snapshot, "sessionId").GetString());
        Assert.Equal("technical", GetProperty(snapshot, "phase").GetString());
        Assert.Single(GetProperty(snapshot, "documents").EnumerateArray());
        Assert.Single(GetProperty(snapshot, "parameters").EnumerateArray());
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

        public async Task<string> InitializeWorkflowRunAsync()
        {
            var sessionId = Guid.NewGuid().ToString("N");
            var repository = new SqliteTaskSessionRepository();
            await repository.CreateSessionAsync(
                DatabaseFilePath,
                sessionId,
                "Interview Prep",
                "Prepare for backend interview.",
                TestContext.Current.CancellationToken);
            await repository.UpsertDocumentAsync(
                DatabaseFilePath,
                sessionId,
                "resume",
                "# Resume\nBackend engineer.",
                "Resume",
                "resume.md",
                TestContext.Current.CancellationToken);
            await repository.UpsertParameterAsync(
                DatabaseFilePath,
                sessionId,
                "response_language",
                "text",
                "English",
                TestContext.Current.CancellationToken);
            return sessionId;
        }

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

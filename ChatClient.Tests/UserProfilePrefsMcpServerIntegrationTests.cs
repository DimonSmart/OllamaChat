using ChatClient.Api.Services;
using ChatClient.Api.Services.BuiltIn;
using ChatClient.Domain.Models;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using System.Text.Json;

namespace ChatClient.Tests;

[Collection("UserProfileMcp")]
public sealed class UserProfilePrefsMcpServerIntegrationTests
{
    [Fact]
    public async Task UserMemoryServer_ProvidesExplicitDurablePreferenceAndMemoryOperations()
    {
        await using var fixture = new UserProfilePrefsMcpFixture();
        var client = await fixture.CreateClientAsync();
        var toolMap = (await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken))
            .ToDictionary(static tool => tool.Name, StringComparer.OrdinalIgnoreCase);

        Assert.Contains("prefs_set", toolMap.Keys);
        Assert.Contains("prefs_delete", toolMap.Keys);
        Assert.Contains("memory_remember", toolMap.Keys);
        Assert.Contains("memory_search", toolMap.Keys);
        Assert.Contains("memory_list", toolMap.Keys);
        Assert.Contains("memory_forget", toolMap.Keys);

        var missing = GetStructuredContent(await CallToolAsync(
            toolMap["prefs_get"],
            new Dictionary<string, object?>
            {
                ["key"] = "displayName"
            }));
        Assert.False(GetProperty(missing, "exists").GetBoolean());
        Assert.False(TryGetProperty(missing, "value", out _));

        await CallToolAsync(toolMap["prefs_set"], new Dictionary<string, object?>
        {
            ["key"] = "displayName",
            ["value"] = "Alice"
        });
        await CallToolAsync(toolMap["prefs_set"], new Dictionary<string, object?>
        {
            ["key"] = "displayName",
            ["value"] = "Dmitry"
        });

        var remembered = GetStructuredContent(await CallToolAsync(
            toolMap["memory_remember"],
            new Dictionary<string, object?> { ["text"] = "User works primarily with .NET." }));
        var memoryId = GetProperty(remembered, "id").GetString();
        Assert.False(string.IsNullOrWhiteSpace(memoryId));

        client = await fixture.RecreateClientAsync();
        toolMap = (await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken))
            .ToDictionary(static tool => tool.Name, StringComparer.OrdinalIgnoreCase);

        var stored = GetStructuredContent(await CallToolAsync(
            toolMap["prefs_get"],
            new Dictionary<string, object?> { ["key"] = "displayName" }));
        Assert.True(GetProperty(stored, "exists").GetBoolean());
        Assert.Equal("Dmitry", GetProperty(stored, "value").GetString());

        var search = GetStructuredContent(await CallToolAsync(
            toolMap["memory_search"],
            new Dictionary<string, object?> { ["query"] = ".net" }));
        Assert.Single(GetProperty(search, "memories").EnumerateArray());

        await CallToolAsync(toolMap["prefs_delete"], new Dictionary<string, object?> { ["key"] = "displayName" });
        await CallToolAsync(toolMap["memory_forget"], new Dictionary<string, object?> { ["id"] = memoryId });

        var afterDelete = GetStructuredContent(await CallToolAsync(
            toolMap["prefs_get"],
            new Dictionary<string, object?> { ["key"] = "displayName" }));
        Assert.False(GetProperty(afterDelete, "exists").GetBoolean());

        var memories = GetStructuredContent(await CallToolAsync(toolMap["memory_list"], []));
        Assert.Empty(GetProperty(memories, "memories").EnumerateArray());

        var storedDocument = await fixture.ReadStoredProfileAsync();
        Assert.False(storedDocument.Values.ContainsKey("displayName"));
        Assert.Empty(storedDocument.Memories);
        Assert.Contains(storedDocument.Definitions, static definition =>
            string.Equals(definition.Key, "displayName", StringComparison.OrdinalIgnoreCase));
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

    private sealed class UserProfilePrefsMcpFixture : IAsyncDisposable
    {
        private const string StorageRootEnvVar = "OLLAMACHAT_STORAGE_ROOT";

        private readonly DirectoryInfo _storageRoot = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "user-profile-mcp", Guid.NewGuid().ToString("N")));
        private readonly ILoggerFactory _loggerFactory = LoggerFactory.Create(static builder => builder.AddDebug().SetMinimumLevel(LogLevel.Debug));
        private readonly string? _originalStorageRoot = Environment.GetEnvironmentVariable(StorageRootEnvVar);
        private McpClient? _client;

        public UserProfilePrefsMcpFixture()
        {
            Environment.SetEnvironmentVariable(StorageRootEnvVar, _storageRoot.FullName);
        }

        public async Task<UserProfilePreferencesDocument> ReadStoredProfileAsync()
        {
            var json = await File.ReadAllTextAsync(ProfilePath);
            return JsonSerializer.Deserialize<UserProfilePreferencesDocument>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))
                   ?? throw new Xunit.Sdk.XunitException("Stored user profile document could not be deserialized.");
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
                ServerId = BuiltInUserMemoryMcpServerTools.Descriptor.Id
            };

            _client = await McpClient.CreateAsync(
                clientTransport: new StdioClientTransport(
                    new StdioClientTransportOptions
                    {
                        Name = BuiltInUserMemoryMcpServerTools.Descriptor.Name,
                        Command = "dotnet",
                        Arguments = McpSessionBindingTransport.AppendArguments(
                            [assemblyPath, "--mcp-builtin", BuiltInUserMemoryMcpServerTools.Descriptor.Key],
                            binding),
                        WorkingDirectory = Path.GetDirectoryName(assemblyPath)!
                    },
                    _loggerFactory),
                clientOptions: new McpClientOptions
                {
                    ClientInfo = new Implementation
                    {
                        Name = "UserProfilePrefsMcpServerIntegrationTests",
                        Version = "1.0.0"
                    },
                    Capabilities = new ClientCapabilities()
                },
                loggerFactory: _loggerFactory,
                cancellationToken: CancellationToken.None);

            return _client;
        }

        public async Task<McpClient> RecreateClientAsync()
        {
            if (_client is not null)
            {
                await _client.DisposeAsync();
                _client = null;
            }

            return await CreateClientAsync();
        }

        public async ValueTask DisposeAsync()
        {
            if (_client is not null)
            {
                await _client.DisposeAsync();
            }

            Environment.SetEnvironmentVariable(StorageRootEnvVar, _originalStorageRoot);

            try
            {
                if (_storageRoot.Exists)
                {
                    _storageRoot.Delete(recursive: true);
                }
            }
            catch
            {
            }

            _loggerFactory.Dispose();
        }

        private string ProfilePath => Path.Combine(_storageRoot.FullName, "UserData", "user_profile.json");

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

[CollectionDefinition("UserProfileMcp", DisableParallelization = true)]
public sealed class UserProfileMcpCollectionDefinition;

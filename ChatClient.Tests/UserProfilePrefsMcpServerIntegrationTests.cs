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
    public async Task UserProfileServer_ExposesExactlyThreeTools_AndPersistsAfterRestart()
    {
        await using var fixture = new UserProfileMcpFixture();
        var client = await fixture.CreateClientAsync();
        var tools = (await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken))
            .ToDictionary(static tool => tool.Name, StringComparer.OrdinalIgnoreCase);

        Assert.Equal(3, tools.Count);
        Assert.Equal(new[] { "profile_get", "profile_get_all", "profile_set" },
            tools.Keys.OrderBy(static name => name, StringComparer.Ordinal).ToArray());
        Assert.DoesNotContain("prefs_get", tools.Keys);
        Assert.DoesNotContain("memory_remember", tools.Keys);

        var missing = GetStructuredContent(await CallToolAsync(tools["profile_get"],
            new() { ["key"] = "unknown" }));
        Assert.False(GetProperty(missing, "exists").GetBoolean());

        var empty = GetStructuredContent(await CallToolAsync(tools["profile_get_all"], []));
        Assert.Empty(GetProperty(empty, "values").EnumerateObject());

        var set = GetStructuredContent(await CallToolAsync(tools["profile_set"],
            new() { ["key"] = " DisplayName ", ["value"] = " Dmitry " }));
        Assert.False(GetProperty(set, "deleted").GetBoolean());
        Assert.Equal("Dmitry", GetProperty(set, "value").GetString());

        client = await fixture.RecreateClientAsync();
        tools = (await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken))
            .ToDictionary(static tool => tool.Name, StringComparer.OrdinalIgnoreCase);
        var stored = GetStructuredContent(await CallToolAsync(tools["profile_get"],
            new() { ["key"] = "displayname" }));
        Assert.True(GetProperty(stored, "exists").GetBoolean());
        Assert.Equal("Dmitry", GetProperty(stored, "value").GetString());

        var all = GetStructuredContent(await CallToolAsync(tools["profile_get_all"], []));
        Assert.Equal("Dmitry", GetProperty(GetProperty(all, "values"), "DisplayName").GetString());

        var deletion = GetStructuredContent(await CallToolAsync(tools["profile_set"],
            new() { ["key"] = "DISPLAYNAME", ["value"] = " " }));
        Assert.True(GetProperty(deletion, "deleted").GetBoolean());
        await CallToolAsync(tools["profile_set"], new() { ["key"] = "displayName", ["value"] = null });

        var after = GetStructuredContent(await CallToolAsync(tools["profile_get_all"], []));
        Assert.Empty(GetProperty(after, "values").EnumerateObject());
        var raw = await File.ReadAllTextAsync(fixture.ProfilePath);
        using var document = JsonDocument.Parse(raw);
        Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
    }

    private static async Task<JsonElement> CallToolAsync(McpClientTool tool, Dictionary<string, object?> arguments)
    {
        var result = await tool.CallAsync(arguments, null, null);
        return JsonSerializer.SerializeToElement(result);
    }

    private static JsonElement GetStructuredContent(JsonElement toolResult)
    {
        if (TryGetProperty(toolResult, "structuredContent", out var structured))
            return TryGetProperty(structured, "result", out var result) ? result : structured;
        throw new Xunit.Sdk.XunitException($"Tool result has no structuredContent: {toolResult}");
    }

    private static JsonElement GetProperty(JsonElement element, string name)
    {
        if (TryGetProperty(element, name, out var value))
            return value;
        throw new Xunit.Sdk.XunitException($"Property '{name}' missing: {element}");
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private sealed class UserProfileMcpFixture : IAsyncDisposable
    {
        private const string StorageRootEnvVar = "OLLAMACHAT_STORAGE_ROOT";
        private readonly DirectoryInfo _storageRoot = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "user-profile-mcp", Guid.NewGuid().ToString("N")));
        private readonly ILoggerFactory _loggerFactory = LoggerFactory.Create(static builder => builder.AddDebug());
        private readonly string? _originalStorageRoot = Environment.GetEnvironmentVariable(StorageRootEnvVar);
        private McpClient? _client;

        public UserProfileMcpFixture() =>
            Environment.SetEnvironmentVariable(StorageRootEnvVar, _storageRoot.FullName);

        public string ProfilePath => Path.Combine(_storageRoot.FullName, "UserData", "user_profile.json");

        public async Task<McpClient> CreateClientAsync()
        {
            if (_client is not null)
                return _client;

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
                    ClientInfo = new Implementation { Name = "UserProfileMcpTests", Version = "1.0.0" },
                    Capabilities = new ClientCapabilities()
                },
                loggerFactory: _loggerFactory,
                cancellationToken: CancellationToken.None);
            return _client;
        }

        public async Task<McpClient> RecreateClientAsync()
        {
            if (_client is not null)
                await _client.DisposeAsync();
            _client = null;
            return await CreateClientAsync();
        }

        public async ValueTask DisposeAsync()
        {
            if (_client is not null)
                await _client.DisposeAsync();
            Environment.SetEnvironmentVariable(StorageRootEnvVar, _originalStorageRoot);
            if (_storageRoot.Exists)
                _storageRoot.Delete(recursive: true);
            _loggerFactory.Dispose();
        }

        private static string ResolveServerAssemblyPath()
        {
            var localCopy = Path.Combine(AppContext.BaseDirectory, "ChatClient.Api.dll");
            if (File.Exists(localCopy))
                return localCopy;
            var projectOutput = Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory, "..", "..", "..", "..",
                "ChatClient.Api", "bin", "Debug", "net10.0", "ChatClient.Api.dll"));
            if (File.Exists(projectOutput))
                return projectOutput;
            throw new FileNotFoundException("Unable to locate ChatClient.Api.dll for MCP tests.");
        }
    }
}

[CollectionDefinition("UserProfileMcp", DisableParallelization = true)]
public sealed class UserProfileMcpCollectionDefinition;

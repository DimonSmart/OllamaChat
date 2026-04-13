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
    public async Task UserProfilePrefsServer_MigratesLegacyFlatFile_AndElicitsDisplayName()
    {
        await using var fixture = new UserProfilePrefsMcpFixture();
        await fixture.WriteLegacyProfileAsync(
            """
            {
              "timezone": "Europe/Madrid"
            }
            """);

        var client = await fixture.CreateClientAsync();
        var toolMap = (await client.ListToolsAsync())
            .ToDictionary(static tool => tool.Name, StringComparer.OrdinalIgnoreCase);

        var firstResult = GetStructuredContent(await CallToolAsync(
            toolMap["prefs_get"],
            new Dictionary<string, object?>
            {
                ["key"] = "displayName"
            }));

        Assert.Equal("displayName", GetProperty(firstResult, "key").GetString());
        Assert.Equal("Alice", GetProperty(firstResult, "value").GetString());
        Assert.Equal("elicited", GetProperty(firstResult, "source").GetString());

        var secondResult = GetStructuredContent(await CallToolAsync(
            toolMap["prefs_get"],
            new Dictionary<string, object?>
            {
                ["key"] = "displayName"
            }));

        Assert.Equal("Alice", GetProperty(secondResult, "value").GetString());
        Assert.Equal("stored", GetProperty(secondResult, "source").GetString());

        var storedDocument = await fixture.ReadStoredProfileAsync();
        Assert.Equal("Alice", storedDocument.Values["displayName"]);
        Assert.Equal("Europe/Madrid", storedDocument.Values["timezone"]);
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

        public async Task WriteLegacyProfileAsync(string json)
        {
            var directory = Path.GetDirectoryName(ProfilePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await File.WriteAllTextAsync(ProfilePath, json);
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
                ServerId = BuiltInUserProfilePrefsServerTools.Descriptor.Id
            };

            _client = await McpClient.CreateAsync(
                clientTransport: new StdioClientTransport(
                    new StdioClientTransportOptions
                    {
                        Name = BuiltInUserProfilePrefsServerTools.Descriptor.Name,
                        Command = "dotnet",
                        Arguments = McpSessionBindingTransport.AppendArguments(
                            [assemblyPath, "--mcp-builtin", BuiltInUserProfilePrefsServerTools.Descriptor.Key],
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
                    Capabilities = new ClientCapabilities
                    {
                        Elicitation = new ElicitationCapability()
                    },
                    Handlers = new McpClientHandlers
                    {
                        ElicitationHandler = static (request, cancellationToken) =>
                        {
                            var result = McpElicitResultFactory.Create(
                                McpElicitationResponse.Accept(new Dictionary<string, object?>
                                {
                                    ["value"] = "Alice"
                                }));
                            return ValueTask.FromResult(result);
                        }
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

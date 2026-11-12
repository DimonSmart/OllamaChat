using OllamaChat.Cli;
using System.Text.Json;

namespace ChatClient.Tests;

public sealed class CliConfigurationStoreTests
{
    [Fact]
    public void LayersInheritUnsetValuesAndCommandWins()
    {
        using var fixture = new Fixture();
        fixture.Global("""
            {
              "agent": "GlobalAgent",
              "server": "LocalOllama",
              "model": "gpt-oss:20b",
              "timeout": "1h",
              "inputs": { "topic": "global", "depth": "full" },
              "permissions": { "read": true, "write": true, "shell": false }
            }
            """);
        fixture.Local("""
            {
              "agent": "ProjectAgent",
              "model": "qwen3:8b",
              "workspace": "./src",
              "inputs": { "topic": "project" },
              "permissions": { "write": false }
            }
            """);
        var store = fixture.Store;
        var options = store.Resolve(CliCommands.Parse(
            ["--agent", "ExplicitAgent", "--model", "qwen3:14b", "--timeout", "30m", "Fix"], false));
        Assert.Equal("ExplicitAgent", options.Agent);
        Assert.Equal("LocalOllama", options.Server);
        Assert.Equal("qwen3:14b", options.Model);
        Assert.Equal(TimeSpan.FromMinutes(30), options.Timeout);
        Assert.Equal(Path.Combine(fixture.WorkingDirectory, "src"), options.Workspace);
        Assert.Equal("Fix", options.Prompt);
        Assert.True(options.AllowRead);
        Assert.False(options.AllowWrite);
        Assert.False(options.AllowShell);
        Assert.Equal("project", options.Inputs["topic"]);
        Assert.Equal("full", options.Inputs["depth"]);

        var workflow = store.Resolve(CliCommands.Parse(["--workflow", "Pipeline", "--input", "topic=cli"], false));
        Assert.Equal("Pipeline", workflow.Workflow);
        Assert.Null(workflow.Agent);
        Assert.Equal("cli", workflow.Inputs["topic"]);
    }

    [Fact]
    public void ProjectCannotIncreasePermissionsButExplicitInvocationCan()
    {
        using var fixture = new Fixture();
        fixture.Global("""
            { "agent": "Developer", "permissions": { "read": true, "write": false, "shell": false } }
            """);
        fixture.Local("""
            { "permissions": { "read": false, "write": true, "shell": true } }
            """);
        var restricted = fixture.Store.Resolve(CliCommands.Parse(["task"], false));
        Assert.False(restricted.AllowRead);
        Assert.False(restricted.AllowWrite);
        Assert.False(restricted.AllowShell);
        var explicitGrant = fixture.Store.Resolve(CliCommands.Parse([
            "task", "--allow-read", "--allow-write", "--allow-shell"
        ], false));
        Assert.True(explicitGrant.AllowRead);
        Assert.True(explicitGrant.AllowWrite);
        Assert.True(explicitGrant.AllowShell);
        Assert.Throws<CliUsageException>(() => fixture.Store.Set("permissions.write", "true", local: true));
    }

    [Fact]
    public void AProjectWorkflowReplacesAGlobalAgentAndWorkspaceIdReplacesPath()
    {
        using var fixture = new Fixture();
        var id = Guid.NewGuid();
        fixture.Global("""{ "agent": "GlobalAgent", "workspace": "../other" }""");
        fixture.Local($$"""{"workflow":"Review","workspaceId":"{{id:D}}"}""");
        var effective = fixture.Store.Resolve(CliCommands.Parse(["--input", "task=x"], false));
        Assert.Equal("Review", effective.Workflow);
        Assert.Null(effective.Agent);
        Assert.Equal(id.ToString("D"), effective.WorkspaceId);
        Assert.Null(effective.Workspace);
    }

    [Fact]
    public void DefaultsToWorkingDirectoryAndFifteenMinutes()
    {
        using var fixture = new Fixture();
        var result = fixture.Store.Resolve(CliCommands.Parse(["--agent", "Reviewer", "Hello"], false));
        Assert.Equal(fixture.WorkingDirectory, result.Workspace);
        Assert.Equal(TimeSpan.FromMinutes(15), result.Timeout);
        Assert.False(result.AllowRead);
        Assert.False(result.AllowWrite);
        Assert.False(result.AllowShell);
    }

    [Fact]
    public void ConfigCommandsWriteEditableJsonAndPreserveUnrelatedKeys()
    {
        using var fixture = new Fixture();
        fixture.Store.Set("agent", "Developer", local: false);
        fixture.Store.Set("server", "LocalOllama", local: false);
        fixture.Store.Set("model", "gpt-oss:20b", local: false);
        fixture.Store.Set("model", "qwen3:8b", local: true);
        fixture.Store.Set("workflow", "Review", local: true);
        Assert.Contains("Review", fixture.Store.Show(localOnly: true, effective: false));
        Assert.Contains("qwen3:8b", fixture.Store.Show(localOnly: false, effective: true));
        var effective = fixture.Store.Resolve(CliCommands.Parse(["Hello"], false));
        Assert.Equal("Review", effective.Workflow);
        Assert.Null(effective.Agent);
        Assert.Equal("LocalOllama", effective.Server);

        fixture.Store.Unset("model", local: true);
        Assert.Equal("gpt-oss:20b",
            fixture.Store.Resolve(CliCommands.Parse(["Hello"], false)).Model);
        using var global = JsonDocument.Parse(File.ReadAllText(fixture.GlobalPath));
        Assert.Equal("LocalOllama", global.RootElement.GetProperty("server").GetString());
    }

    [Fact]
    public void BadJsonIsNeverSilentlyReset()
    {
        using var fixture = new Fixture();
        const string bad = "{ bad json";
        fixture.Local(bad);
        Assert.Throws<CliUsageException>(() => fixture.Store.Resolve(CliCommands.Parse(["hello"], false)));
        Assert.Throws<CliUsageException>(() => fixture.Store.Set("agent", "Reader", local: true));
        Assert.Equal(bad, File.ReadAllText(fixture.ProjectPath));
    }

    [Fact]
    public void ProjectCannotOverrideHostOrStorageConfiguration()
    {
        using var fixture = new Fixture();
        fixture.Local("""
            { "Storage": { "RootPath": "/untrusted" }, "agent": "Reader" }
            """);
        var effective = fixture.Store.Resolve(CliCommands.Parse(["Hello"], false));
        Assert.Equal("Reader", effective.Agent);
        Assert.Equal(fixture.WorkingDirectory, effective.Workspace);
    }

    [Fact]
    public void ObsoleteMcpFlagsAreRejectedAndNotStored()
    {
        Assert.Throws<CliUsageException>(() => CliCommands.Parse(
            ["--agent", "Reader", "--allow-mcp", "WebSearch:search", "Hello"], false));
        Assert.Throws<CliUsageException>(() => CliCommands.Parse(
            ["--agent", "Reader", "--no-mcp", "Hello"], false));
        using var fixture = new Fixture();
        Assert.Throws<CliUsageException>(() =>
            fixture.Store.Set("permissions.mcpTools", "anything", false));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(),
            "ollamachat-config-tests-" + Guid.NewGuid().ToString("N"));
        public string WorkingDirectory { get; }
        public string GlobalPath => Path.Combine(_root, "UserData", "cli_settings.json");
        public string ProjectPath => Path.Combine(WorkingDirectory, ".ollamachat.json");
        public CliConfigurationStore Store => new(GlobalPath, WorkingDirectory);

        public Fixture()
        {
            WorkingDirectory = Path.Combine(_root, "workspace");
            Directory.CreateDirectory(WorkingDirectory);
            Directory.CreateDirectory(Path.GetDirectoryName(GlobalPath)!);
        }

        public void Global(string json) => File.WriteAllText(GlobalPath, json);
        public void Local(string json) => File.WriteAllText(ProjectPath, json);
        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}

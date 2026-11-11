using ChatClient.Api;
using ChatClient.Api.Services.AgentRuntime;
using ChatClient.Application.Services;
using ChatClient.Domain.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;
using OllamaChat.Cli;
using System.Diagnostics;

namespace ChatClient.Tests;

public sealed class CliCommandsTests
{
    [Fact]
    public void ParsingAndValidation()
    {
        var command = CliCommands.Parse(["run", "--agent", "A", "--prompt", "hello", "--timeout", "1h"], false);
        Assert.Equal(TimeSpan.FromHours(1), command.Timeout);
        Assert.Equal("hello", command.Prompt);
        Assert.Equal(TimeSpan.FromMinutes(15),
            CliCommands.Parse(["run", "--agent", "A", "--prompt-file", "task.md"], false).Timeout);
        Assert.True(CliCommands.Parse(["run", "--agent", "A"], true).FromStdin);
        Assert.Equal(CliCommandKind.ListAgents, CliCommands.Parse(["agents", "list"], false).Kind);
        Assert.Equal(CliCommandKind.Help, CliCommands.Parse(["--help"], false).Kind);

        string[][] invalid =
        [
            ["run", "--prompt", "hello"],
            ["run", "--agent", "A"],
            ["run", "--agent", "A", "--prompt", " "],
            ["run", "--agent", "A", "--prompt", "ok", "--prompt-file", "file"],
            ["run", "--agent", "A", "--prompt", "ok", "--timeout", "0s"],
            ["run", "--agent", "A", "--prompt", "ok", "--timeout", "abc"],
            ["run", "--agent", "A", "--prompt", "ok", "--no-such-option"],
            ["run", "--agent", "A", "--prompt", "ok", "--overwrite"]
        ];
        foreach (var args in invalid)
            Assert.Throws<CliUsageException>(() => CliCommands.Parse(args, false));
        Assert.Throws<CliUsageException>(() =>
            CliCommands.Parse(["run", "--agent", "A", "--prompt", "ok"], true));
    }

    [Fact]
    public void LookupIsCaseInsensitiveButGuidIsExact()
    {
        var a = new AgentTemplateDefinition { AgentName = "Reviewer" };
        var b = new AgentTemplateDefinition { AgentName = "reviewer" };
        Assert.Equal(2, CliCommands.FindAgents([a, b], "REVIEWER").Count);
        Assert.Single(CliCommands.FindAgents([a, b], a.Id.ToString("D")));
        Assert.Empty(CliCommands.FindAgents([a, b], "Unknown"));
    }

    [Fact]
    public void CliDiContainsNoHostedServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCliAgentServices();
        Assert.Empty(services.Where(item => item.ServiceType == typeof(IHostedService)));
    }

    [Fact]
    public async Task ListingAndNameErrorsDoNotLaunchRuntime()
    {
        var a = new AgentTemplateDefinition { AgentName = "Same" };
        var b = new AgentTemplateDefinition { AgentName = "same" };
        var (provider, executor) = CreateServices([a, b]);
        using (provider)
        {
            var output = new StringWriter();
            Assert.Equal(0, await CliCommands.ExecuteAsync(new CliArguments(CliCommandKind.ListAgents),
                provider, TextReader.Null, output, new StringWriter(), TestContext.Current.CancellationToken));
            Assert.Contains(a.Id.ToString("D"), output.ToString());
            foreach (var name in new[] { "SAME", "Missing" })
            {
                var error = new StringWriter();
                Assert.Equal(3, await CliCommands.ExecuteAsync(
                    CliCommands.Parse(["run", "--agent", name, "--prompt", "task"], false),
                    provider, TextReader.Null, new StringWriter(), error, TestContext.Current.CancellationToken));
                Assert.NotEmpty(error.ToString());
            }
            executor.Verify(e => e.ExecuteAsync(It.IsAny<HeadlessAgentRunRequest>(),
                It.IsAny<CancellationToken>()), Times.Never);
        }
    }

    [Fact]
    public async Task ExecutesSinglePromptWithAbsoluteWorkspaceAndCleanStdout()
    {
        var agent = new AgentTemplateDefinition { AgentName = "Reader" };
        var (provider, executor) = CreateServices([agent]);
        HeadlessAgentRunRequest? seen = null;
        executor.Setup(e => e.ExecuteAsync(It.IsAny<HeadlessAgentRunRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((HeadlessAgentRunRequest request, CancellationToken _) =>
            {
                seen = request;
                return new HeadlessAgentRunResult("Reader", "run", "готово", null, null);
            });
        using (provider)
        {
            var output = new StringWriter();
            var error = new StringWriter();
            var command = CliCommands.Parse(
                ["run", "--agent", "reader", "--prompt", "Review", "--workspace", "."], false);
            Assert.Equal(0, await CliCommands.ExecuteAsync(command, provider, TextReader.Null,
                output, error, TestContext.Current.CancellationToken));
            Assert.Equal("готово", output.ToString());
            Assert.Equal("", error.ToString());
            Assert.Equal(agent.Id, seen!.AgentId);
            Assert.Equal("Review", seen.Prompt);
            Assert.Equal(Path.GetFullPath("."), seen.DirectPath);
            Assert.Null(seen.WorkspaceId);
        }
    }

    [Fact]
    public async Task MapsPreflightAndRuntimeErrorsWithoutEchoingSecrets()
    {
        var (provider, executor) = CreateServices([new AgentTemplateDefinition { AgentName = "A" }]);
        using (provider)
        {
            foreach (var (code, exitCode) in new[] {
                ("headless_capability_unsupported", 3),
                ("workspace_unavailable", 3),
                ("agent_preflight_failed", 3),
                ("execution_failed", 4) })
            {
                executor.Setup(e => e.ExecuteAsync(It.IsAny<HeadlessAgentRunRequest>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new HeadlessAgentRunResult("A", null, null, code, "SECRET-123"));
                var stderr = new StringWriter();
                Assert.Equal(exitCode, await CliCommands.ExecuteAsync(
                    CliCommands.Parse(["run", "--agent", "A", "--prompt", "task"], false),
                    provider, TextReader.Null, new StringWriter(), stderr, TestContext.Current.CancellationToken));
                Assert.DoesNotContain("SECRET-123", stderr.ToString());
            }
        }
    }

    [Fact]
    public async Task ReadsPromptFileAndStdin()
    {
        var (provider, executor) = CreateServices([new AgentTemplateDefinition { AgentName = "A" }]);
        var captured = new List<string>();
        executor.Setup(e => e.ExecuteAsync(It.IsAny<HeadlessAgentRunRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((HeadlessAgentRunRequest request, CancellationToken _) =>
            {
                captured.Add(request.Prompt);
                return new HeadlessAgentRunResult("A", null, "done", null, null);
            });
        var file = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(file, "file task");
            using (provider)
            {
                await CliCommands.ExecuteAsync(
                    CliCommands.Parse(["run", "--agent", "A", "--prompt-file", file], false),
                    provider, TextReader.Null, new StringWriter(), new StringWriter(),
                    TestContext.Current.CancellationToken);
                await CliCommands.ExecuteAsync(
                    CliCommands.Parse(["run", "--agent", "A"], true),
                    provider, new StringReader("stdin task"), new StringWriter(), new StringWriter(),
                    TestContext.Current.CancellationToken);
            }
            Assert.Equal(["file task", "stdin task"], captured);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public async Task SavesUtf8AtomicallyAndRequiresOverwrite()
    {
        var file = Path.GetTempFileName();
        try
        {
            await Assert.ThrowsAsync<IOException>(() =>
                CliCommands.SaveOutputAsync(file, "new", false, TestContext.Current.CancellationToken));
            await CliCommands.SaveOutputAsync(file, "Привет", true, TestContext.Current.CancellationToken);
            Assert.Equal("Привет", await File.ReadAllTextAsync(file));
            Assert.Equal("Привет", System.Text.Encoding.UTF8.GetString(await File.ReadAllBytesAsync(file)));
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(file)!,
                "." + Path.GetFileName(file) + ".*.tmp"));
        }
        finally { File.Delete(file); }
    }

    private static (ServiceProvider provider, Mock<IHeadlessAgentExecutor> executor) CreateServices(
        IReadOnlyCollection<AgentTemplateDefinition> agents)
    {
        foreach (var agent in agents)
        {
            agent.LlmId ??= Guid.NewGuid();
            agent.ModelName ??= "test";
        }
        var servers = new Mock<ILlmServerConfigService>();
        servers.Setup(s => s.GetByIdAsync(It.IsAny<Guid>()))
            .ReturnsAsync((Guid id) => new LlmServerConfig { Id = id });
        var templates = new Mock<IAgentTemplateService>();
        templates.Setup(s => s.GetAllAsync()).ReturnsAsync(agents);
        var executor = new Mock<IHeadlessAgentExecutor>();
        var services = new ServiceCollection();
        services.AddSingleton(templates.Object);
        services.AddSingleton(servers.Object);
        services.AddSingleton(executor.Object);
        return (services.BuildServiceProvider(), executor);
    }
}

public sealed class CliProcessTests
{
    [Fact]
    public async Task StandaloneProcessListsAgentsAndExitsWithoutModifyingStorage()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "OllamaChat.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        var bin = Path.Combine(directory.FullName, "OllamaChat.Cli", "bin", "Release", "net10.0");
        if (!File.Exists(Path.Combine(bin, "ollamachat.dll")))
            bin = Path.Combine(directory.FullName, "OllamaChat.Cli", "bin", "Debug", "net10.0");
        var dll = Path.Combine(bin, "ollamachat.dll");
        Assert.True(File.Exists(dll), "Build the CLI project before running the process test.");
        var root = Path.Combine(Path.GetTempPath(), "ollamachat-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo("dotnet")
            {
                ArgumentList = { dll, "agents", "list" },
                WorkingDirectory = Path.GetTempPath(),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            process.StartInfo.Environment["OLLAMACHAT_STORAGE_ROOT"] = root;
            Assert.True(process.Start());
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var stdout = process.StandardOutput.ReadToEndAsync(deadline.Token);
            var stderr = process.StandardError.ReadToEndAsync(deadline.Token);
            await process.WaitForExitAsync(deadline.Token);
            Assert.Equal(0, process.ExitCode);
            Assert.StartsWith("ID", await stdout);
            Assert.Equal("", await stderr);
            Assert.Empty(Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}

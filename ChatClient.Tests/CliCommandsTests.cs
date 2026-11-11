using ChatClient.Api;
using ChatClient.Api.Services;
using ChatClient.Api.Services.AgentRuntime;
using ChatClient.Application.Services;
using ChatClient.Domain.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;
using OllamaChat.Cli;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

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
        Assert.Equal("ok", CliCommands.Parse(["run", "--agent", "A", "--prompt", "ok"], true).Prompt);
        Assert.False(CliCommands.Parse(["run", "--agent", "A", "--prompt", "ok"], true).FromStdin);
        Assert.False(CliCommands.Parse(["run", "--agent", "A", "--prompt-file", "task.md"], true).FromStdin);
    }

    [Fact]
    public void ParsesWorkflowOptionsAndDoesNotConsumeImplicitStdin()
    {
        var command = CliCommands.Parse([
            "run", "--workflow", "Review", "--input", "reviewMode=standard",
            "--allow-read", "--allow-write", "--allow-shell",
            "--allow-mcp", "WebSearch:search", "--sandbox", "Dev",
            "--workspace-id", Guid.NewGuid().ToString("D")
        ], stdinRedirected: true);

        Assert.Equal("Review", command.Workflow);
        Assert.Null(command.Agent);
        Assert.False(command.FromStdin);
        Assert.Equal("standard", command.Inputs["reviewMode"]);
        Assert.Equal(["WebSearch:search"], command.AllowedMcpTools);
        Assert.True(command.AllowRead);
        Assert.True(command.AllowWrite);
        Assert.True(command.AllowShell);

        Assert.Throws<CliUsageException>(() =>
            CliCommands.Parse(["run", "--agent", "A", "--workflow", "B", "--prompt", "p"], false));
        Assert.Throws<CliUsageException>(() =>
            CliCommands.Parse(["run", "--workflow", "W", "--input", "a=1", "--input", "a=2"], false));
        Assert.Throws<CliUsageException>(() =>
            CliCommands.Parse(["run", "--agent", "A", "--prompt", "p", "--allow-shell"], false));
        Assert.Throws<CliUsageException>(() =>
            CliCommands.Parse(["run", "--workflow", "W", "--server", "server"], false));
        Assert.Throws<CliUsageException>(() =>
            CliCommands.Parse(["run", "--workflow", "W", "--stdin"], false));
    }

    [Fact]
    public void WorkflowLookupRejectsAmbiguousNames()
    {
        var first = new SavedWorkflowDefinition { Id = Guid.NewGuid(), DisplayName = "Research" };
        var second = new SavedWorkflowDefinition { Id = Guid.NewGuid(), DisplayName = "research" };
        Assert.Equal(2, CliCommands.FindWorkflows([first, second], "RESEARCH").Count);
        Assert.Single(CliCommands.FindWorkflows([first, second], first.Id.ToString("D")));
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
        var workflows = new Mock<IWorkflowDefinitionService>();
        workflows.Setup(w => w.GetAllAsync())
            .ReturnsAsync(Array.Empty<SavedWorkflowDefinition>());
        services.AddSingleton(workflows.Object);
        services.AddSingleton(executor.Object);
        return (services.BuildServiceProvider(), executor);
    }
}

public sealed class CliProcessTests
{
    [Fact]
    public async Task Run_StandaloneProcessUsesRealDIAndMockOpenAiServer()
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

        var root = Path.Combine(Path.GetTempPath(), "ollamachat-cli-process-" + Guid.NewGuid().ToString("N"));
        var userData = Path.Combine(root, "UserData");
        Directory.CreateDirectory(userData);
        using var portReservation = new TcpListener(IPAddress.Loopback, 0);
        portReservation.Start();
        var port = ((IPEndPoint)portReservation.LocalEndpoint).Port;
        portReservation.Stop();

        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var serverTask = Task.Run(async () =>
        {
            var context = await listener.GetContextAsync();
            Assert.Equal("/v1/chat/completions", context.Request.Url?.AbsolutePath);
            Assert.Equal("POST", context.Request.HttpMethod);
            context.Response.StatusCode = 200;
            context.Response.ContentType = "text/event-stream";
            const string response = """
                data: {"id":"chatcmpl-test","object":"chat.completion.chunk","created":1730000000,"model":"fixture-model","choices":[{"index":0,"delta":{"role":"assistant","content":"Process fixture answer"},"finish_reason":null}]}

                data: {"id":"chatcmpl-test","object":"chat.completion.chunk","created":1730000000,"model":"fixture-model","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}

                data: [DONE]


                """;
            var bytes = Encoding.UTF8.GetBytes(response.Replace("\r\n", "\n"));
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes);
            context.Response.Close();
        });
        var serverId = Guid.NewGuid();
        var savedAgent = new AgentTemplateDefinition
        {
            Id = Guid.NewGuid(),
            AgentName = "ProcessFixture",
            Content = "Return a short response.",
            LlmId = serverId,
            ModelName = "fixture-model"
        };
        var server = new LlmServerConfig
        {
            Id = serverId,
            Name = "LocalFixture",
            ServerType = ServerType.ChatGpt,
            ApiKey = "test-key",
            BaseUrl = $"http://127.0.0.1:{port}/v1"
        };
        await File.WriteAllTextAsync(
            Path.Combine(userData, "agent_templates.json"), JsonSerializer.Serialize(new[] { savedAgent }));
        await File.WriteAllTextAsync(
            Path.Combine(userData, "llm_servers.json"), JsonSerializer.Serialize(new[] { server }));
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo("dotnet")
            {
                ArgumentList = { dll, "run", "--agent", savedAgent.Id.ToString("D"), "--prompt", "ping" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            process.StartInfo.Environment["OLLAMACHAT_STORAGE_ROOT"] = root;
            Assert.True(process.Start());
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var stdoutTask = process.StandardOutput.ReadToEndAsync(deadline.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(deadline.Token);
            await process.WaitForExitAsync(deadline.Token);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            var diagnosticsPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "OllamaChat", "logs", "cli-errors.log");
            var diagnosticTail = File.Exists(diagnosticsPath)
                ? (await File.ReadAllTextAsync(diagnosticsPath))[^Math.Min(
                    2500, (int)new FileInfo(diagnosticsPath).Length)..]
                : "no diagnostic log";
            Assert.True(process.ExitCode == 0,
                $"CLI exited with {process.ExitCode}. stderr: {stderr}. Diagnostics: {diagnosticTail}");
            Assert.Equal("Process fixture answer", stdout);
            Assert.Equal(string.Empty, stderr);
            await serverTask.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(File.Exists(Path.Combine(userData, "saved_chats.json")));
            Assert.False(File.Exists(Path.Combine(userData, "scheduled_tasks.json")));
        }
        finally
        {
            listener.Stop();
            Directory.Delete(root, recursive: true);
        }
    }

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
        var userData = Path.Combine(root, "UserData");
        Directory.CreateDirectory(userData);
        var agent = new AgentTemplateDefinition { AgentName = "ExistingGuiAgent" };
        var savedAgents = Path.Combine(userData, "agent_templates.json");
        var originalJson = System.Text.Json.JsonSerializer.Serialize(new[] { agent });
        await File.WriteAllTextAsync(savedAgents, originalJson);
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
            Assert.Contains(agent.Id.ToString("D"), await stdout);
            Assert.Contains("ExistingGuiAgent", await stdout);
            Assert.Equal("", await stderr);
            Assert.Equal(originalJson, await File.ReadAllTextAsync(savedAgents));
            Assert.Single(Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}

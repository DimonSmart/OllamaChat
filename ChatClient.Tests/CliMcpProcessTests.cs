using ChatClient.Domain.Models;
using OllamaChat.Cli;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace ChatClient.Tests;

public sealed class CliMcpProcessTests
{
    [Fact]
    public async Task SavedAgentBindingProvidesOnlyItsConfiguredMcpToolWithoutCliGrants()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "OllamaChat.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        var bin = Path.Combine(directory.FullName, "OllamaChat.Cli", "bin", "Release", "net10.0");
        if (!File.Exists(Path.Combine(bin, "ollamachat.dll")))
            bin = Path.Combine(directory.FullName, "OllamaChat.Cli", "bin", "Debug", "net10.0");
        var dll = Path.Combine(bin, "ollamachat.dll");
        Assert.True(File.Exists(dll));

        var root = Path.Combine(Path.GetTempPath(), "ollamachat-mcp-cli-" + Guid.NewGuid().ToString("N"));
        var userData = Path.Combine(root, "UserData");
        Directory.CreateDirectory(userData);

        using var portReservation = new TcpListener(IPAddress.Loopback, 0);
        portReservation.Start();
        var port = ((IPEndPoint)portReservation.LocalEndpoint).Port;
        portReservation.Stop();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();

        var seenRequests = new List<string>();
        var serverTask = Task.Run(async () =>
        {
            for (var index = 0; index < 2; index++)
            {
                var context = await listener.GetContextAsync().WaitAsync(TimeSpan.FromSeconds(40));
                Assert.Equal("/v1/chat/completions", context.Request.Url?.AbsolutePath);
                using var reader = new StreamReader(context.Request.InputStream);
                var posted = await reader.ReadToEndAsync();
                seenRequests.Add(posted);
                var first = index == 0;
                var response = first
                    ? """
                        data: {"id":"chatcmpl-mcp","object":"chat.completion.chunk","created":1730000000,"model":"fixture-model","choices":[{"index":0,"delta":{"role":"assistant","tool_calls":[{"index":0,"id":"call_math","type":"function","function":{"name":"evaluate_expression","arguments":"{\"expression\":\"6*7\"}"}}]},"finish_reason":null}]}

                        data: {"id":"chatcmpl-mcp","object":"chat.completion.chunk","created":1730000000,"model":"fixture-model","choices":[{"index":0,"delta":{},"finish_reason":"tool_calls"}]}

                        data: [DONE]


                        """
                    : """
                        data: {"id":"chatcmpl-final","object":"chat.completion.chunk","created":1730000001,"model":"fixture-model","choices":[{"index":0,"delta":{"role":"assistant","content":"42"},"finish_reason":null}]}

                        data: {"id":"chatcmpl-final","object":"chat.completion.chunk","created":1730000001,"model":"fixture-model","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}

                        data: [DONE]


                        """;
                var bytes = Encoding.UTF8.GetBytes(response.Replace("\r\n", "\n"));
                context.Response.StatusCode = 200;
                context.Response.ContentType = "text/event-stream";
                context.Response.ContentLength64 = bytes.Length;
                await context.Response.OutputStream.WriteAsync(bytes);
                context.Response.Close();
            }
        });

        var serverId = Guid.NewGuid();
        var agent = new AgentTemplateDefinition
        {
            Id = Guid.NewGuid(),
            AgentName = "McpFixture",
            Content = "Use evaluate_expression.",
            LlmId = serverId,
            ModelName = "fixture-model",
            McpServerBindings =
            [
                new McpServerSessionBinding
                {
                    ServerId = Guid.Parse("76ca15c0-4f2d-4a76-8d32-70fdd6dd5083"),
                    ServerName = "Built-in Math MCP Server",
                    Enabled = true,
                    SelectAllTools = false,
                    SelectedTools = ["evaluate_expression"]
                }
            ]
        };
        var server = new LlmServerConfig
        {
            Id = serverId,
            Name = "LocalFixture",
            ServerType = ServerType.ChatGpt,
            ApiKey = "test-key",
            BaseUrl = $"http://127.0.0.1:{port}/v1"
        };
        await File.WriteAllTextAsync(Path.Combine(userData, "agent_templates.json"), JsonSerializer.Serialize(new[] { agent }));
        await File.WriteAllTextAsync(Path.Combine(userData, "llm_servers.json"), JsonSerializer.Serialize(new[] { server }));
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo("dotnet")
            {
                ArgumentList = { dll, "--agent", agent.Id.ToString("D"), "Compute 6*7" },
                WorkingDirectory = root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            process.StartInfo.Environment["OLLAMACHAT_STORAGE_ROOT"] = root;
            Assert.True(process.Start());
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(50));
            var stdoutTask = process.StandardOutput.ReadToEndAsync(deadline.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(deadline.Token);
            await process.WaitForExitAsync(deadline.Token);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            Assert.Equal(0, process.ExitCode);
            Assert.Equal("42", stdout);
            Assert.Equal("", stderr);
            await serverTask.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(2, seenRequests.Count);
            using var firstRequest = JsonDocument.Parse(seenRequests[0]);
            var tools = firstRequest.RootElement.GetProperty("tools")
                .EnumerateArray().Select(tool => tool.GetProperty("function").GetProperty("name").GetString()).ToArray();
            Assert.Contains("evaluate_expression", tools);
            Assert.DoesNotContain("get_current_time", tools);
            Assert.Contains("42", seenRequests[1]);
        }
        finally
        {
            listener.Stop();
            Directory.Delete(root, recursive: true);
        }
    }
}

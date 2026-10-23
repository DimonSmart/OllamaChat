using System.Runtime.CompilerServices;
using ChatClient.Tests.LlmCassettes;
using Microsoft.Extensions.AI;
#pragma warning disable MAAI001
using Microsoft.Agents.AI;
#pragma warning restore MAAI001

#pragma warning disable MAAI001

namespace ChatClient.Tests;

/// <summary>Integration coverage: real Harness + real file-access tools, not mocked tool results.</summary>
public sealed class LlmCassetteHarnessTests
{
    private const string Readme = "# Cassette Test Workspace\nProject: Blue Parrot\nPurpose: Verify real file access.\n";
    private const string Prompt = "Read README.md and tell me the project name.";

    [Fact]
    public async Task Harness_ReadFile_ActualToolIsExecutedOnReplay_AndMutationIsDetected()
    {
        var root = Path.Combine(Path.GetTempPath(), "ollamachat-harness-cassette-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "cassette.json");
        var file = Path.Combine(root, "README.md");
        try
        {
            await File.WriteAllTextAsync(file, Readme);
            var fake = new ScriptedLlm();
            var recorded = new LlmCassette(path, record: true, workspaceRoot: root);
            recorded.ClaimClient();
            await RunHarnessAsync(new RecordingChatClient(fake, recorded), root);
            Assert.True(fake.SawRealToolResult);
            await recorded.CompleteAsync(TestContext.Current.CancellationToken);

            var replay = new LlmCassette(path, workspaceRoot: root);
            replay.ClaimClient();
            await RunHarnessAsync(new ReplayChatClient(replay), root);
            await replay.CompleteAsync(TestContext.Current.CancellationToken);

            await File.WriteAllTextAsync(file, Readme.Replace("Blue Parrot", "Red Parrot"));
            var mutated = new LlmCassette(path, workspaceRoot: root);
            mutated.ClaimClient();
            try
            {
                await RunHarnessAsync(new ReplayChatClient(mutated), root);
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("LLM cassette mismatch", StringComparison.Ordinal))
            {
                // Some Harness versions propagate provider errors, others surface them as run events.
            }
            await Assert.ThrowsAsync<InvalidOperationException>(() => mutated.CompleteAsync());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task RunHarnessAsync(IChatClient client, string root)
    {
        var agent = client.AsHarnessAgent(new HarnessAgentOptions
        {
            Name = "Cassette Test Agent",
            ChatOptions = new ChatOptions { ModelId = "cassette-model", Temperature = 0 },
            FileAccessStore = new FileSystemAgentFileStore(root),
            FileAccessProviderOptions = new FileAccessProviderOptions
            {
                DisableWriteTools = true,
                DisableReadOnlyToolApproval = true
            },
            DisableTodoProvider = true,
            DisableAgentModeProvider = true,
            DisableWebSearch = true,
            DisableFileMemory = true,
            DisableAgentSkillsProvider = true,
            DisableCompaction = true
        });
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);
        await foreach (var _ in agent.RunStreamingAsync(
                           [new ChatMessage(ChatRole.User, Prompt)], session,
                           cancellationToken: TestContext.Current.CancellationToken))
        {
        }
    }

    private sealed class ScriptedLlm : IChatClient
    {
        private int _calls;
        public bool SawRealToolResult { get; private set; }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType == typeof(ChatClientMetadata)
                ? new ChatClientMetadata("cassette", null, "cassette-model")
                : null;
        public void Dispose() { }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("Use the streaming Harness path.");

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var step = Interlocked.Increment(ref _calls);
            if (step == 1)
            {
                Assert.Contains(options?.Tools ?? [], t => t is AIFunctionDeclaration fn && fn.Name == "file_access_read");
                yield return new ChatResponseUpdate(ChatRole.Assistant,
                    [new FunctionCallContent("read-call", "file_access_read",
                        new Dictionary<string, object?> { ["fileName"] = "README.md" })])
                { MessageId = "assistant-call", FinishReason = ChatFinishReason.ToolCalls };
            }
            else if (step == 2)
            {
                var result = messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>()
                    .Single(r => r.CallId == "read-call");
                SawRealToolResult = result.Result?.ToString()?.Contains(Readme, StringComparison.Ordinal) == true;
                Assert.True(SawRealToolResult, "The Harness must execute the actual file-access read tool.");
                yield return new ChatResponseUpdate(ChatRole.Assistant, "Blue Parrot")
                { MessageId = "assistant-answer", FinishReason = ChatFinishReason.Stop };
            }
            else
            {
                throw new InvalidOperationException("Unexpected extra LLM call from Harness.");
            }
            await Task.CompletedTask;
        }
    }
}

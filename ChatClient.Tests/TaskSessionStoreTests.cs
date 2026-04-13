using ChatClient.Api.Services;
using ChatClient.Api.Services.BuiltIn;
using ChatClient.Domain.Models;
using ChatClient.Infrastructure.Services.TaskSessions;

namespace ChatClient.Tests;

public sealed class TaskSessionStoreTests
{
    [Fact]
    public async Task AppendTurnAsync_AssignsUniqueIncreasingSequences_WhenCallsRunConcurrently()
    {
        var databasePath = CreateTaskStorePath();
        var databaseDirectory = Directory.GetParent(databasePath);

        try
        {
            var store = CreateTaskSessionStore(databasePath);
            var session = await store.CreateSessionAsync("Concurrent turns", null, CancellationToken.None);
            const int appendCount = 24;
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var appendTasks = Enumerable.Range(1, appendCount)
                .Select(async index =>
                {
                    await release.Task;
                    return await store.AppendTurnAsync(
                        session.SessionId,
                        "assistant",
                        $"Turn {index}",
                        "assistant",
                        CancellationToken.None);
                })
                .ToArray();

            release.SetResult();
            var turns = await Task.WhenAll(appendTasks);

            var sequences = turns
                .Select(turn => turn.Sequence)
                .OrderBy(sequence => sequence)
                .ToArray();

            Assert.Equal(
                Enumerable.Range(1, appendCount).Select(static value => (long)value).ToArray(),
                sequences);

            var storedTurns = await store.ListTurnsAsync(session.SessionId, 0, 200, CancellationToken.None);
            Assert.Equal(appendCount, storedTurns.Count);
            Assert.Equal(sequences, storedTurns.Select(turn => turn.Sequence).ToArray());
        }
        finally
        {
            if (databaseDirectory?.Exists == true)
            {
                try
                {
                    databaseDirectory.Delete(recursive: true);
                }
                catch
                {
                }
            }
        }
    }

    private static TaskSessionStore CreateTaskSessionStore(string databasePath)
    {
        var binding = new McpServerSessionBinding();
        binding.Parameters[TaskSessionStore.DatabaseFileParameter] = databasePath;
        return new TaskSessionStore(
            new McpServerSessionContext(binding),
            new SqliteTaskSessionRepository());
    }

    private static string CreateTaskStorePath()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "OllamaChat.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        return Path.Combine(tempDirectory, "task-sessions.db");
    }
}

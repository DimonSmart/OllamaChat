using ChatClient.Domain.Models;
using System.Collections.Concurrent;

namespace ChatClient.Api.Client.Services.Agentic;

internal interface IRagRetrievalTraceSink
{
    IDisposable BeginTurn(string turnId);
    void Record(RagRetrievalTrace trace);
    IReadOnlyList<RagRetrievalTrace> Drain(string turnId);
}

internal sealed class RagRetrievalTraceBuffer : IRagRetrievalTraceSink
{
    private readonly ConcurrentDictionary<string, ConcurrentQueue<RagRetrievalTrace>> _turns = new(StringComparer.Ordinal);
    private readonly AsyncLocal<string?> _currentTurn = new();

    public IDisposable BeginTurn(string turnId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(turnId);
        _turns[turnId] = new ConcurrentQueue<RagRetrievalTrace>();
        var previous = _currentTurn.Value;
        _currentTurn.Value = turnId;
        return new TurnScope(this, turnId, previous);
    }

    public void Record(RagRetrievalTrace trace)
    {
        ArgumentNullException.ThrowIfNull(trace);
        var turnId = _currentTurn.Value;
        if (turnId is not null && _turns.TryGetValue(turnId, out var traces))
            traces.Enqueue(trace);
    }

    public IReadOnlyList<RagRetrievalTrace> Drain(string turnId)
    {
        if (!_turns.TryGetValue(turnId, out var traces))
            return [];
        var drained = new List<RagRetrievalTrace>();
        while (traces.TryDequeue(out var trace))
            drained.Add(trace);
        return drained;
    }

    private sealed class TurnScope(RagRetrievalTraceBuffer owner, string turnId, string? previous) : IDisposable
    {
        public void Dispose()
        {
            owner._turns.TryRemove(turnId, out _);
            owner._currentTurn.Value = previous;
        }
    }
}

internal static class RagToolNames
{
    public const string SearchAgentKnowledge = "search_agent_knowledge";
}

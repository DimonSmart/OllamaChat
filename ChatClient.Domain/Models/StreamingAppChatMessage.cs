using System.Collections.Generic;
using System.Text;

namespace ChatClient.Domain.Models;

public class StreamingAppChatMessage(
    string initialContent,
    DateTime msgDateTime,
    AppChatRole role,
    List<ToolInvocationViewState>? toolInvocations = null,
    string? agentId = null,
    string? agentName = null) : IAppChatMessage
{
    private readonly StringBuilder _contentBuilder = new(initialContent);
    public string Content => _contentBuilder.ToString();
    public DateTime MsgDateTime { get; private set; } = msgDateTime;
    public AppChatRole Role { get; private set; } = role;
    public string? Statistics { get; private set; } = string.Empty;
    public ChatRunUsage? Usage { get; private set; }
    public bool IsCanceled { get; private set; }
    public IReadOnlyList<AppChatMessageFile> Files { get; private set; } = [];
    private readonly List<ToolInvocationViewState> _toolInvocations = toolInvocations ?? [];
    public IReadOnlyCollection<ToolInvocationViewState> ToolInvocations => _toolInvocations.AsReadOnly();
    private readonly List<RagRetrievalTrace> _ragRetrievals = [];
    public IReadOnlyCollection<RagRetrievalTrace> RagRetrievals => _ragRetrievals.AsReadOnly();
    public string? AgentId { get; private set; } = agentId;
    public string? AgentName { get; private set; } = agentName;

    public int ApproximateTokenCount { get; set; }

    public Guid Id { get; private set; } = Guid.NewGuid();

    public bool IsStreaming => true;

    public bool Equals(IAppChatMessage? other)
    {
        if (other is null)
            return false;
        return Id == other.Id;
    }

    public override bool Equals(object? obj)
    {
        return obj is IAppChatMessage other && Equals(other);
    }

    public override int GetHashCode()
    {
        return Id.GetHashCode();
    }

    public void Append(string? text)
    {
        _contentBuilder.Append(text);
    }

    public void ResetContent()
    {
        _contentBuilder.Clear();
    }

    public void SetAgentName(string? name)
    {
        AgentName = name;
    }

    public void SetAgentId(string? id)
    {
        AgentId = id;
    }
    public void SetStatistics(string stats)
    {
        Statistics = stats;
    }

    public void StartToolInvocation(ToolInvocationViewState invocation)
    {
        var index = _toolInvocations.FindIndex(item => item.CallId == invocation.CallId);
        if (index >= 0)
        {
            _toolInvocations[index] = invocation;
        }
        else
        {
            _toolInvocations.Add(invocation);
        }
    }

    public void SetUsage(ChatRunUsage? usage)
    {
        Usage = usage;
    }

    public void UpdateToolInvocation(ToolInvocationViewState invocation)
    {
        StartToolInvocation(invocation);
    }

    public void AddOrUpdateRagRetrieval(RagRetrievalTrace trace)
    {
        ArgumentNullException.ThrowIfNull(trace);
        var index = _ragRetrievals.FindIndex(item => item.Id == trace.Id);
        if (index >= 0)
            _ragRetrievals[index] = trace;
        else
            _ragRetrievals.Add(trace);
    }

    public void SetCanceled()
    {
        IsCanceled = true;
    }
}

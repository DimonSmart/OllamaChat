using System.Collections.Generic;
using System.Text;

using Microsoft.Extensions.AI;

namespace ChatClient.Shared.Models;

public class StreamingAppChatMessage(string initialContent, DateTime msgDateTime, ChatRole role, List<FunctionCallRecord>? functionCalls = null, string? agentName = null) : IAppChatMessage
{
    private readonly StringBuilder _contentBuilder = new(initialContent);
    public string Content => _contentBuilder.ToString();
    public DateTime MsgDateTime { get; private set; } = msgDateTime;
    public ChatRole Role { get; private set; } = role;
    public string? Statistics { get; private set; } = string.Empty;
    public bool IsCanceled { get; private set; }
    public IReadOnlyList<ChatMessageFile> Files { get; private set; } = [];
    private readonly List<FunctionCallRecord> _functionCalls = functionCalls ?? [];
    public IReadOnlyCollection<FunctionCallRecord> FunctionCalls => _functionCalls.AsReadOnly();
    public string? AgentName { get; private set; } = agentName;

    public Guid Id { get; private set; } = Guid.NewGuid();

    /// <summary>
    /// Indicates whether this message is currently streaming.
    /// Always true for streaming messages.
    /// </summary>
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
    public void SetStatistics(string stats)
    {
        Statistics = stats;
    }

    public void AddFunctionCall(FunctionCallRecord record)
    {
        _functionCalls.Add(record);
    }

    public void SetCanceled()
    {
        IsCanceled = true;
    }
}

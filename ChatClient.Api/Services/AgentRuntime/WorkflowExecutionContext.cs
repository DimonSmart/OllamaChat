using ChatClient.Api.Client.Services.Agentic;
using ChatClient.Application.Services.Agentic;
using ChatClient.Domain.Models;

namespace ChatClient.Api.Services.AgentRuntime;

internal sealed class WorkflowExecutionContext
{
    public List<IAppChatMessage> Messages { get; } = [];

    public Dictionary<Guid, string?> SpeakerIdsByMessageId { get; } = [];

    public List<string> AssistantSpeakerIds { get; } = [];

    public Dictionary<Guid, StreamingAppChatMessage> ActiveStreams { get; } = [];

    public Dictionary<Guid, string?> ActiveSpeakerIdsByStreamId { get; } = [];

    public Dictionary<Guid, int> StreamContentLengths { get; } = [];

    public HashSet<Guid> EmittedCompletedMessageIds { get; } = [];

    // Framework event sources are translated immediately to the participant's stable ID.
    public Dictionary<string, string> ParticipantIdsByEventSource { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, string> ParticipantNamesById { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    public void RegisterParticipant(string participantId, string displayName, string? executorId)
    {
        ParticipantIdsByEventSource[participantId] = participantId;
        if (!string.IsNullOrWhiteSpace(executorId))
        {
            ParticipantIdsByEventSource[executorId] = participantId;
        }

        // The framework can expose AuthorName for output events. This is a lookup fallback,
        // never a workflow identity: all downstream state retains participantId.
        ParticipantIdsByEventSource[displayName] = participantId;
        ParticipantNamesById[participantId] = displayName;
    }
}

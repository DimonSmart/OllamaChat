using ChatClient.Domain.Models;
using ChatClient.Domain.Models.StopAgents;

using Microsoft.AspNetCore.Components;

namespace ChatClient.Api.Client.Components;

public sealed record StopAgentEditorContext(IStopAgentOptions Options, IReadOnlyList<AgentDescription> Agents);

public abstract class StopAgentParametersBase : ComponentBase
{
    [Parameter]
    public StopAgentEditorContext Context { get; set; } = default!;
}


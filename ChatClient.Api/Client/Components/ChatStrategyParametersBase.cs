using ChatClient.Domain.Models;
using ChatClient.Domain.Models.ChatStrategies;

using Microsoft.AspNetCore.Components;

namespace ChatClient.Api.Client.Components;

public sealed record ChatStrategyEditorContext(IChatStrategyOptions Options, IReadOnlyList<AgentDescription> Agents);

public abstract class ChatStrategyParametersBase : ComponentBase
{
    [Parameter]
    public ChatStrategyEditorContext Context { get; set; } = default!;
}


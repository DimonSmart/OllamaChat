using ChatClient.Api.Client.ViewModels;
using ChatClient.Application.Services.Agentic;

namespace ChatClient.Api.Client.Services.Agentic;

public interface IAgenticChatViewModelService : IAsyncDisposable
{
    IReadOnlyList<AppChatMessageViewModel> Messages { get; }
    bool IsAnswering { get; }
    AgentSessionStateViewModel? SessionState { get; }
    event Action<bool>? AnsweringStateChanged;
    event Action? ChatReset;
    event Action? SessionStateChanged;
    event Func<AppChatMessageViewModel, Task>? MessageAdded;
    event Func<AppChatMessageViewModel, MessageUpdateOptions, Task>? MessageUpdated;
}

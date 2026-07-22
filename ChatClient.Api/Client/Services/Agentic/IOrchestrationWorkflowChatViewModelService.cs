namespace ChatClient.Api.Client.Services.Agentic;

public interface IOrchestrationWorkflowChatViewModelService : IAgenticChatViewModelService
{
    event Func<ChatClient.Api.Client.ViewModels.AppChatMessageViewModel, Task>? MessageDeleted;
}

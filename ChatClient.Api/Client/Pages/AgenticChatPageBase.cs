using ChatClient.Api.Client.Components;
using ChatClient.Api.Client.Services;
using ChatClient.Api.Client.Services.Agentic;
using ChatClient.Api.Client.ViewModels;
using ChatClient.Api.Services;
using ChatClient.Application.Services;
using ChatClient.Application.Services.Agentic;
using ChatClient.Domain.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MudBlazor;

namespace ChatClient.Api.Client.Pages;

public abstract class AgenticChatPageBase : ComponentBase, IAsyncDisposable
{
    protected const int UpdateIntervalMs = 500;

    protected bool isLLMAnswering { get; set; }
    protected bool isLoadingInitialData = true;
    protected bool chatStarted = false;
    protected Guid? lastSavedChatId;
    protected string lastSavedChatTitle = string.Empty;
    protected UserSettings userSettings = new();
    protected AgenticChatSession? chatSessionView;

    [Inject]
    protected IJSRuntime JSRuntime { get; set; } = null!;

    [Inject]
    protected NavigationManager NavigationManager { get; set; } = null!;

    [Inject]
    protected IDialogService DialogService { get; set; } = null!;

    [Inject]
    protected IAgenticChatViewModelService ChatViewModelService { get; set; } = null!;

    [Inject]
    protected IChatEngineSessionService ChatService { get; set; } = null!;

    [Inject]
    protected IAgentDescriptionService AgentService { get; set; } = null!;

    [Inject]
    protected IUserSettingsService UserSettingsService { get; set; } = null!;

    [Inject]
    protected ISavedChatService SavedChatService { get; set; } = null!;

    [Inject]
    protected ISnackbar Snackbar { get; set; } = null!;

    [Inject]
    protected IMcpUserInteractionService McpUserInteractionService { get; set; } = null!;

    [Inject]
    protected ILoggerFactory LoggerFactory { get; set; } = null!;

    protected ILogger Logger => _logger ??= LoggerFactory.CreateLogger(GetType());

    [Parameter, SupplyParameterFromQuery(Name = "saved")]
    public Guid? SavedChatId { get; set; }

    private ILogger? _logger;
    private StreamingDebouncer _renderDebouncer = null!;
    private Func<AppChatMessageViewModel, Task>? _messageAddedHandler;
    private Func<AppChatMessageViewModel, MessageUpdateOptions, Task>? _messageUpdatedHandler;
    private Func<AppChatMessageViewModel, Task>? _messageDeletedHandler;
    private IDisposable? elicitationHandlerRegistration;

    protected virtual bool CanSendMessages => true;

    protected virtual string ElicitationLogContext => "agentic chat";

    protected virtual Task OnBeforeInitialLoadAsync() => Task.CompletedTask;

    protected abstract Task LoadAgentsAsync();

    protected abstract Task LoadUserSettingsAsync();

    protected abstract Task LoadSavedChatAsync(Guid savedChatId);

    protected override async Task OnInitializedAsync()
    {
        isLoadingInitialData = true;
        StateHasChanged();

        await OnBeforeInitialLoadAsync();

        elicitationHandlerRegistration = McpUserInteractionService.RegisterElicitationHandler(
            McpInteractionScope.Chat,
            HandleElicitationAsync);

        await LoadAgentsAsync();
        await LoadUserSettingsAsync();

        ChatService.AnsweringStateChanged += OnAnsweringStateChanged;
        ChatViewModelService.ChatReset += OnChatReset;
        _renderDebouncer = new StreamingDebouncer(UpdateIntervalMs, () => InvokeAsync(StateHasChanged));
        _messageAddedHandler = message => InvokeAsync(() => OnMessageAddedAsync(message));
        _messageUpdatedHandler = (message, options) => InvokeAsync(() => OnMessageUpdatedAsync(message, options));
        _messageDeletedHandler = message => InvokeAsync(() => OnMessageDeleted(message));
        ChatViewModelService.MessageAdded += _messageAddedHandler;
        ChatViewModelService.MessageUpdated += _messageUpdatedHandler;
        ChatViewModelService.MessageDeleted += _messageDeletedHandler;

        isLoadingInitialData = false;
        StateHasChanged();
    }

    protected override async Task OnParametersSetAsync()
    {
        if (SavedChatId.HasValue && SavedChatId != lastSavedChatId)
            await LoadSavedChatAsync(SavedChatId.Value);
    }

    protected virtual void OnAnsweringStateChanged(bool answering)
    {
        isLLMAnswering = answering;
        _ = InvokeAsync(StateHasChanged);
    }

    protected virtual async Task OnMessageAddedAsync(AppChatMessageViewModel message)
    {
        StateHasChanged();
        await ScrollToBottomAsync();
    }

    protected virtual Task OnMessageUpdatedAsync(AppChatMessageViewModel message, MessageUpdateOptions options) =>
        _renderDebouncer.TriggerAsync(options);

    protected virtual void OnMessageDeleted(AppChatMessageViewModel message)
    {
        StateHasChanged();
    }

    protected virtual void OnChatReset()
    {
        chatStarted = false;
        lastSavedChatId = null;
        lastSavedChatTitle = string.Empty;
        _ = InvokeAsync(StateHasChanged);
    }

    protected async Task SaveCurrentChat()
    {
        if (!ChatService.Messages.Any())
        {
            Snackbar.Add("Nothing to save", Severity.Info);
            return;
        }

        var parameters = new DialogParameters
        {
            ["InitialTitle"] = SavedChatId.HasValue ? lastSavedChatTitle : string.Empty
        };
        var dialog = await DialogService.ShowAsync<SaveChatDialog>("Save Chat", parameters);
        var result = await dialog.Result;
        if (result?.Canceled != false)
            return;

        var title = result.Data as string ?? string.Empty;

        var participants = ChatService.AgentDescriptions
            .Select(agent => new SavedChatParticipant(agent.AgentId, agent.AgentName, ChatRole.Assistant))
            .ToList();
        participants.Add(new SavedChatParticipant(userSettings.UserName.ToLowerInvariant(), userSettings.UserName, ChatRole.User));

        var agentMap = ChatService.AgentDescriptions.ToDictionary(agent => agent.AgentName, agent => agent.AgentId, StringComparer.OrdinalIgnoreCase);

        var messages = ChatService.Messages
            .Where(message => !message.IsStreaming && !message.IsCanceled && message.Role != ChatRole.System)
            .Where(message => !string.IsNullOrWhiteSpace(message.Content) || message.Files.Count > 0)
            .Select(message => new SavedChatMessage(
                message.Id,
                message.Content,
                message.MsgDateTime,
                message.Role,
                message.Role == ChatRole.Assistant && message.AgentName != null && agentMap.TryGetValue(message.AgentName, out var agentId) ? agentId : null,
                message.AgentName))
            .ToList();

        var chatId = SavedChatId ?? Guid.NewGuid();
        var chat = new SavedChat(chatId, title, DateTime.UtcNow, messages, participants);
        await SavedChatService.SaveAsync(chat);

        var isNew = !SavedChatId.HasValue;
        SavedChatId = chatId;
        lastSavedChatTitle = title;
        Snackbar.Add("Chat saved", Severity.Success);
        if (isNew)
            NavigationManager.NavigateTo(NavigationManager.GetUriWithQueryParameter("saved", chatId));
    }

    protected async Task SendChatMessageAsync((string text, IReadOnlyList<AppChatMessageFile> files) messageData)
    {
        if (string.IsNullOrWhiteSpace(messageData.text) || isLLMAnswering || !CanSendMessages)
            return;

        await ChatService.SendAsync(messageData.text.Trim(), messageData.files);
        await ScrollToBottomAsync();
    }

    protected Task ScrollToBottomAsync() =>
        chatSessionView?.ScrollToBottomAsync() ?? Task.CompletedTask;

    protected Task Cancel() =>
        ChatService.CancelAsync();

    protected async Task DeleteMessage(AppChatMessageViewModel message)
    {
        if (isLLMAnswering)
            return;

        await ChatService.DeleteMessageAsync(message.Id);
    }

    protected Task CopyVisibleMessage(AppChatMessageViewModel message) =>
        JSRuntime.InvokeVoidAsync("copyText", message.Content).AsTask();

    protected async Task CopyRawMessage(AppChatMessageViewModel message)
    {
        if (!string.IsNullOrEmpty(message.RawContent))
            await JSRuntime.InvokeVoidAsync("copyText", message.RawContent);
    }

    protected Task CopyChatAsync(ChatFormat format)
    {
        var text = ChatTranscriptFormatter.Format(ChatViewModelService.Messages, format);
        return JSRuntime.InvokeVoidAsync("copyText", text).AsTask();
    }

    private async Task<McpElicitationResponse> HandleElicitationAsync(
        McpElicitationPrompt prompt,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            var parameters = new DialogParameters
            {
                ["Prompt"] = prompt
            };
            var options = new DialogOptions
            {
                MaxWidth = MaxWidth.Small,
                FullWidth = true,
                BackdropClick = false,
                CloseOnEscapeKey = true,
                CloseButton = true
            };

            IDialogReference? dialog = null;
            await InvokeAsync(async () =>
            {
                dialog = await DialogService.ShowAsync<McpElicitationDialog>(
                    $"MCP input: {prompt.ServerName}",
                    parameters,
                    options);
            });

            if (dialog is null)
                return McpElicitationResponse.Cancel;

            Logger.LogInformation(
                "Elicitation dialog opened in {Context}. Server: {ServerName}. Mode: {Mode}. Fields: {FieldCount}.",
                ElicitationLogContext,
                prompt.ServerName,
                prompt.Mode,
                prompt.Fields.Count);

            using var registration = cancellationToken.Register(() =>
            {
                _ = InvokeAsync(() => dialog.Close(DialogResult.Ok(McpElicitationResponse.Cancel)));
            });

            var result = await dialog.Result;
            if (result?.Canceled != false)
            {
                Logger.LogInformation(
                    "Elicitation dialog closed in {Context} with cancel. Server: {ServerName}.",
                    ElicitationLogContext,
                    prompt.ServerName);
                return McpElicitationResponse.Cancel;
            }

            var response = result.Data as McpElicitationResponse ?? McpElicitationResponse.Cancel;
            Logger.LogInformation(
                "Elicitation dialog completed in {Context}. Server: {ServerName}. Action: {Action}.",
                ElicitationLogContext,
                prompt.ServerName,
                response.Action);
            return response;
        }
        catch (OperationCanceledException)
        {
            Logger.LogInformation(
                "Elicitation dialog canceled by token in {Context}. Server: {ServerName}.",
                ElicitationLogContext,
                prompt.ServerName);
            return McpElicitationResponse.Cancel;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to render elicitation dialog in {Context}.", ElicitationLogContext);
            Snackbar.Add($"Failed to handle MCP user prompt: {ex.Message}", Severity.Warning);
            return McpElicitationResponse.Cancel;
        }
    }

    public virtual async ValueTask DisposeAsync()
    {
        ChatService.AnsweringStateChanged -= OnAnsweringStateChanged;
        ChatViewModelService.ChatReset -= OnChatReset;
        if (_messageAddedHandler is not null)
            ChatViewModelService.MessageAdded -= _messageAddedHandler;
        if (_messageUpdatedHandler is not null)
            ChatViewModelService.MessageUpdated -= _messageUpdatedHandler;
        if (_messageDeletedHandler is not null)
            ChatViewModelService.MessageDeleted -= _messageDeletedHandler;
        elicitationHandlerRegistration?.Dispose();

        await ChatService.CancelAsync();
        ChatService.ResetChat();
    }
}

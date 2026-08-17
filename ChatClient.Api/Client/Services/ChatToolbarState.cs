namespace ChatClient.Api.Client.Services;

public sealed class ChatToolbarState
{
    private Func<Task>? toggleSessionState;
    private Func<Task>? toggleTrace;
    private Func<ChatFormat, Task>? copyConversation;

    public event Action? Changed;

    public bool IsAvailable { get; private set; }

    public bool HasSessionState { get; private set; }

    public bool IsSessionStateOpen { get; private set; }
    public bool HasTrace { get; private set; }
    public bool IsTraceOpen { get; private set; }

    public void Update(
        bool hasSessionState,
        bool isSessionStateOpen,
        bool hasTrace,
        bool isTraceOpen,
        Func<Task> toggleSessionState,
        Func<Task> toggleTrace,
        Func<ChatFormat, Task> copyConversation)
    {
        var hasChanged = !IsAvailable ||
                         HasSessionState != hasSessionState ||
                         IsSessionStateOpen != isSessionStateOpen || HasTrace != hasTrace || IsTraceOpen != isTraceOpen;
        IsAvailable = true;
        HasSessionState = hasSessionState;
        IsSessionStateOpen = isSessionStateOpen;
        HasTrace = hasTrace;
        IsTraceOpen = isTraceOpen;
        this.toggleSessionState = toggleSessionState;
        this.toggleTrace = toggleTrace;
        this.copyConversation = copyConversation;
        if (hasChanged)
            Changed?.Invoke();
    }

    public void Clear()
    {
        IsAvailable = false;
        HasSessionState = false;
        IsSessionStateOpen = false;
        HasTrace = false;
        IsTraceOpen = false;
        toggleSessionState = null;
        toggleTrace = null;
        copyConversation = null;
        Changed?.Invoke();
    }

    public Task ToggleSessionStateAsync() => toggleSessionState?.Invoke() ?? Task.CompletedTask;
    public Task ToggleTraceAsync() => toggleTrace?.Invoke() ?? Task.CompletedTask;

    public Task CopyConversationAsync(ChatFormat format) =>
        copyConversation?.Invoke(format) ?? Task.CompletedTask;
}

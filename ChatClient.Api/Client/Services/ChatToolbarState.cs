namespace ChatClient.Api.Client.Services;

public sealed class ChatToolbarState
{
    private Func<Task>? toggleSessionState;
    private Func<ChatFormat, Task>? copyConversation;

    public event Action? Changed;

    public bool IsAvailable { get; private set; }

    public bool HasSessionState { get; private set; }

    public bool IsSessionStateOpen { get; private set; }

    public void Update(
        bool hasSessionState,
        bool isSessionStateOpen,
        Func<Task> toggleSessionState,
        Func<ChatFormat, Task> copyConversation)
    {
        var hasChanged = !IsAvailable ||
                         HasSessionState != hasSessionState ||
                         IsSessionStateOpen != isSessionStateOpen;
        IsAvailable = true;
        HasSessionState = hasSessionState;
        IsSessionStateOpen = isSessionStateOpen;
        this.toggleSessionState = toggleSessionState;
        this.copyConversation = copyConversation;
        if (hasChanged)
            Changed?.Invoke();
    }

    public void Clear()
    {
        IsAvailable = false;
        HasSessionState = false;
        IsSessionStateOpen = false;
        toggleSessionState = null;
        copyConversation = null;
        Changed?.Invoke();
    }

    public Task ToggleSessionStateAsync() => toggleSessionState?.Invoke() ?? Task.CompletedTask;

    public Task CopyConversationAsync(ChatFormat format) =>
        copyConversation?.Invoke(format) ?? Task.CompletedTask;
}

using ChatClient.Api.Services.Sandbox;
using ChatClient.Application.Services;
using ChatClient.Application.Services.Agentic;
using ChatClient.Application.Services.AgentRuntime;
using ChatClient.Application.Services.Sandbox;
using ChatClient.Domain.Models;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using MudBlazor;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

#pragma warning disable MAAI001

namespace ChatClient.Api.Client.Services.Agentic;

public sealed class UnifiedAgentRuntimeChatSessionService(
    IAgentRunner agentRunner,
    IAgentDefinitionCatalog definitionCatalog,
    IAgentRunContextFactory runContextFactory,
    IChatEngineStreamingBridge streamingBridge,
    ILogger<UnifiedAgentRuntimeChatSessionService> logger,
    IAgentTemplateService agentTemplateService,
    ISandboxSessionFactory sandboxSessionFactory,
    AgenticRuntimeAgentFactory runtimeAgentFactory,
    HarnessResponseEventProjector responseEventProjector,
    ISavedChatService? savedChatService = null,
    IChatTitleGenerator? chatTitleGenerator = null,
    HarnessTraceSession? harnessTraceSession = null,
    IAgentSessionDefinitionResolver? definitionResolver = null,
    ISnackbar? snackbar = null) : IChatEngineSessionService, IAsyncDisposable
{
    private readonly AppChat _chat = new();
    private readonly Dictionary<string, StreamingAppChatMessage> _activeStreamsByRuntimeMessageId =
        new(StringComparer.Ordinal);
    private static readonly HarnessRunUsageAggregator runUsageAggregator = new();
    private readonly HashSet<string> _completedRuntimeMessageIds = new(StringComparer.Ordinal);
    private ChatEngineSessionStartRequest? _parameters;
    private ActiveChatSessionInfo? _activeSession;
    private CancellationTokenSource? _cancellationTokenSource;
    private AIAgent? _directAgent;
    private HarnessAgentRuntimeDefinition? _directRuntimeDefinition;
    private AgentSession? _directSession;
    private string _directRuntimeAgentId = string.Empty;
    private IReadOnlyList<string> _directAvailableModes = [];
    private SessionWorkspaceAgentFileStore? _directFileAccessStore;
    private FileAccessProviderProfile? _directFileAccessProfile;
    private AgentFileStore? _directFileMemoryStore;
    private AgentSessionCompactionViewModel? _directCompaction;
    private IReadOnlyList<AgentSessionSkillViewModel> _directSkills = [];
    private IReadOnlyList<string> _directSkillDiagnostics = [];
    private IReadOnlyList<AgentSessionBackgroundAgentViewModel> _directBackgroundAgents = [];
    private SandboxSessionHandle? _sandboxSession;
    private ISessionToolApprovalCoordinator? _toolApprovalCoordinator;
    private SessionToolApprovalPolicy? _toolApprovalPolicy;
    private IReadOnlyDictionary<string, AgenticRegisteredTool> _directToolMetadata =
        new Dictionary<string, AgenticRegisteredTool>(StringComparer.OrdinalIgnoreCase);
    private TaskCompletionSource? _activeRunCompletion;
    private ToolApprovalRequestContent? _pendingToolApprovalRequest;
    private readonly object _lifecycleLock = new();
    private readonly SemaphoreSlim _runSetupGate = new(1, 1);
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private long _generation;
    private bool _resetting;
    private SavedChatHandle? _savedChat;
    private bool _savedChatPersistenceSuppressed;
    private bool _persistenceHealthy = true;

    public event Action<bool>? AnsweringStateChanged;
    public event Action? ChatReset;
    public event Func<IAppChatMessage, Task>? MessageAdded;
    public event Func<IAppChatMessage, bool, Task>? MessageUpdated;
    public event Action? SessionStateChanged;

    public bool IsAnswering { get; private set; }

    public bool RequiresReset { get; private set; }

    public bool HasActiveSession => _activeSession is not null;

    public ActiveChatSessionInfo? ActiveSession => _activeSession is null
        ? null
        : SnapshotActiveSession(_activeSession);

    public ToolApprovalRequestViewModel? PendingToolApproval { get; private set; }

    public Guid Id => _chat.Id;

    public IReadOnlyCollection<AgentExecutionSpec> Agents => _chat.Agents;

    public ObservableCollection<IAppChatMessage> Messages => _chat.Messages;

    IReadOnlyCollection<IAppChatMessage> IChatSessionService.Messages => _chat.Messages;

    public async Task StartAsync(
        ChatEngineSessionStartRequest request,
        CancellationToken cancellationToken = default,
        IProgress<ChatSessionStartProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.RuntimeReference is null && request.Agents.Count == 0)
        {
            throw new ArgumentException(
                "A runtime reference or at least one resolved agent must be provided.",
                nameof(request));
        }

        if (!await _startGate.WaitAsync(0, cancellationToken))
        {
            throw new InvalidOperationException("Chat session startup is already in progress.");
        }

        try
        {
            progress?.Report(new ChatSessionStartProgress(
                ChatSessionStartStage.ResettingPreviousSession,
                "Resetting previous session..."));
            await ResetAsync(cancellationToken);

            progress?.Report(new ChatSessionStartProgress(
                ChatSessionStartStage.PreparingRuntime,
                "Preparing runtime..."));
            _parameters = request.Snapshot();
            _chat.Reset();
            var sessionId = _chat.Id.ToString("N");
            ClearRunLocalState();
            _toolApprovalCoordinator = new SessionToolApprovalCoordinator();
            _toolApprovalPolicy = new SessionToolApprovalPolicy();
            _toolApprovalPolicy.SetWorkspace(_sandboxSession?.WorkspacePath ?? request.Overrides.WorkspacePath);
            _toolApprovalCoordinator.PendingRequestChanged += HandleCoordinatorPendingRequestChanged;
            _chat.SetAgents(request.RuntimeParticipant is { } participant
                ? [new AgentExecutionSpec
                {
                    RuntimeAgentId = participant.Id,
                    AgentName = participant.Name,
                    Summary = participant.Description,
                    ShortName = participant.AvatarText
                }]
                : request.Agents.Select(static agent => agent.Agent.Clone()));
            ChatReset?.Invoke();

            if (request.RuntimeReference is { } runtimeReference)
            {
                progress?.Report(new ChatSessionStartProgress(
                    ChatSessionStartStage.ResolvingDefinition,
                    "Resolving agent definition..."));
                var descriptor = await definitionCatalog.GetRequiredAsync(runtimeReference, cancellationToken);
                _sandboxSession = await CreateSandboxSessionAsync(
                    request,
                    descriptor,
                    sessionId,
                    cancellationToken,
                    progress);

                if (runtimeReference.Kind == AgentDefinitionKind.SavedAgent)
                {
                    progress?.Report(new ChatSessionStartProgress(
                        ChatSessionStartStage.CreatingAgentSession,
                        "Creating agent session..."));
                    await CreateDirectConversationAsync(request, cancellationToken);
                }
            }

            _activeSession = CreateActiveSession(_parameters);
        }
        catch
        {
            await ResetAsync(cancellationToken);
            throw;
        }
        finally
        {
            _startGate.Release();
        }
    }

    public async Task<AgentSessionStateViewModel?> GetSessionStateAsync(
        CancellationToken cancellationToken = default)
    {
        var agent = _directAgent;
        var session = _directSession;
        var sandboxSession = _sandboxSession;
        var workflowWorkspacePath = _parameters?.Overrides.WorkspacePath;
        if (agent is null || session is null)
        {
            return sandboxSession is null && string.IsNullOrWhiteSpace(workflowWorkspacePath)
                ? null
                : new AgentSessionStateViewModel(
                    null,
                    [],
                    false,
                    false,
                    [],
                    null,
                    null,
                    sandboxSession is null ? null : new AgentSessionSandboxViewModel(
                        sandboxSession.ProfileId,
                        sandboxSession.ProfileName,
                        sandboxSession.ProviderType,
                        sandboxSession.Summary.Image,
                        sandboxSession.WorkspacePath,
                        sandboxSession.Instance.State));
        }

        var todoProvider = agent.GetService<TodoProvider>();
        var modeProvider = agent.GetService<AgentModeProvider>();
        var todos = todoProvider is null
            ? []
            : (await todoProvider.GetAllTodosAsync(session, cancellationToken))
                .Select(static todo => new AgentSessionTodoItemViewModel(todo.Id, todo.Title, todo.Description, todo.IsComplete))
                .ToList();
        var mode = modeProvider is null ? null : await modeProvider.GetModeAsync(session, cancellationToken);

        var fileAccess = _directFileAccessStore is null || _directFileAccessProfile is null ? null : new AgentSessionFileAccessViewModel(
            _directFileAccessStore.WorkspacePath,
            _directFileAccessProfile.Name,
            _directFileAccessProfile.AccessMode,
            _directFileAccessProfile.RequireReadApproval,
            _directFileAccessProfile.RequireWriteApproval);
        var sandbox = sandboxSession is null ? null : new AgentSessionSandboxViewModel(
            sandboxSession.ProfileId,
            sandboxSession.ProfileName,
            sandboxSession.ProviderType,
            sandboxSession.Summary.Image,
            sandboxSession.WorkspacePath,
            sandboxSession.Instance.State);
        var fileMemory = await GetFileMemoryStateAsync(agent, session, cancellationToken);

        if (fileAccess is not null && sandbox is not null && !HaveSameWorkspace(fileAccess.WorkspacePath, sandbox.WorkspacePath))
        {
            logger.LogWarning(
                "File Access workspace and Sandbox workspace are inconsistent. FileAccessWorkspace={FileAccessWorkspace}, SandboxWorkspace={SandboxWorkspace}",
                fileAccess.WorkspacePath,
                sandbox.WorkspacePath);
        }

        return new AgentSessionStateViewModel(
            mode,
            _directAvailableModes,
            todoProvider is not null,
            modeProvider is not null,
            todos,
            fileAccess,
            fileMemory,
            sandbox,
            _directCompaction,
            _directSkills,
            _directSkillDiagnostics,
            _directBackgroundAgents);
    }

    public async Task SetFileAccessWorkspaceAsync(string workspace, CancellationToken cancellationToken = default)
    {
        await _runSetupGate.WaitAsync(cancellationToken);
        try
        {
            throw new InvalidOperationException("Workspace cannot be changed after the conversation has started. Start a new conversation to use another workspace.");
        }
        finally { _runSetupGate.Release(); }
    }

    public async Task<string?> ReadFileMemoryAsync(string name, CancellationToken cancellationToken = default)
    {
        var state = await GetFileMemoryStateAsync(_directAgent, _directSession, cancellationToken);
        return state is null || !state.Enabled || string.IsNullOrWhiteSpace(state.WorkingFolder) || _directFileMemoryStore is null || !state.Files.Any(file => string.Equals(file.Name, name, StringComparison.Ordinal))
            ? null
            : await _directFileMemoryStore.ReadAsync(CombineMemoryPath(state.WorkingFolder, name), cancellationToken);
    }

    public async Task ClearFileMemoryAsync(CancellationToken cancellationToken = default)
    {
        var state = await GetFileMemoryStateAsync(_directAgent, _directSession, cancellationToken);
        if (state is null || !state.Enabled || string.IsNullOrWhiteSpace(state.WorkingFolder) || _directFileMemoryStore is null)
            return;

        foreach (var entry in await _directFileMemoryStore.ListChildrenAsync(state.WorkingFolder, cancellationToken))
        {
            if (entry.Type == FileStoreEntry.File)
                await _directFileMemoryStore.DeleteAsync(CombineMemoryPath(state.WorkingFolder, entry.Name), cancellationToken);
        }

        SessionStateChanged?.Invoke();
    }

    public async Task SetAgentModeAsync(string mode, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(mode))
        {
            throw new ArgumentException("An agent mode is required.", nameof(mode));
        }

        await _runSetupGate.WaitAsync(cancellationToken);
        try
        {
            AIAgent? agent;
            AgentSession? session;
            IReadOnlyList<string> availableModes;
            lock (_lifecycleLock)
            {
                if (_resetting || IsAnswering || RequiresReset || PendingToolApproval is not null)
                {
                    throw new InvalidOperationException("The agent mode cannot be changed while this conversation is unavailable.");
                }

                agent = _directAgent;
                session = _directSession;
                availableModes = _directAvailableModes;
            }

            if (agent is null || session is null)
            {
                throw new InvalidOperationException("A direct agent session is not available.");
            }

            var requestedMode = mode.Trim();
            if (!availableModes.Contains(requestedMode, StringComparer.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Agent mode '{requestedMode}' is not available for this conversation.");
            }

            var modeProvider = agent.GetService<AgentModeProvider>()
                ?? throw new InvalidOperationException("The direct agent does not have an Agent Mode Provider.");
            await modeProvider.SetModeAsync(session, requestedMode, cancellationToken);
            SessionStateChanged?.Invoke();
        }
        finally
        {
            _runSetupGate.Release();
        }
    }

    public async Task ResetAsync(CancellationToken cancellationToken = default)
    {
        _savedChat = null;
        _savedChatPersistenceSuppressed = false;
        harnessTraceSession?.Clear();
        Task? activeRun;
        lock (_lifecycleLock)
        {
            _resetting = true;
        }

        await _runSetupGate.WaitAsync(cancellationToken);
        try
        {
            lock (_lifecycleLock)
            {
                Interlocked.Increment(ref _generation);
                _cancellationTokenSource?.Cancel();
                activeRun = _activeRunCompletion?.Task;
            }
        }
        finally
        {
            _runSetupGate.Release();
        }

        if (activeRun is not null)
        {
            await activeRun.WaitAsync(cancellationToken);
        }

        SandboxSessionHandle? sandboxSession;
        HarnessAgentRuntimeDefinition? runtimeDefinition;
        lock (_lifecycleLock)
        {
            runtimeDefinition = _directRuntimeDefinition;
            _directRuntimeDefinition = null;
            _directAgent = null;
            _directSession = null;
            _directRuntimeAgentId = string.Empty;
            _directFileAccessStore = null;
            _directFileAccessProfile = null;
            _directFileMemoryStore = null;
            _directCompaction = null;
            _directSkills = [];
            _directSkillDiagnostics = [];
            _directBackgroundAgents = [];
            sandboxSession = _sandboxSession;
            _sandboxSession = null;
            _directAvailableModes = [];
            _directToolMetadata = new Dictionary<string, AgenticRegisteredTool>(StringComparer.OrdinalIgnoreCase);
            _pendingToolApprovalRequest = null;
            if (_toolApprovalCoordinator is not null)
            {
                _toolApprovalCoordinator.PendingRequestChanged -= HandleCoordinatorPendingRequestChanged;
                _toolApprovalCoordinator.CancelPending();
            }
            _toolApprovalCoordinator = null;
            _toolApprovalPolicy = null;
            PendingToolApproval = null;
            _chat.Reset();
            ClearRunLocalState();
            _parameters = null;
            _activeSession = null;
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = null;
            _activeRunCompletion = null;
            RequiresReset = false;
            _resetting = false;
        }

        runtimeDefinition?.Dispose();

        if (sandboxSession is not null)
        {
            try
            {
                await sandboxSession.DisposeAsync();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Sandbox cleanup failed during session reset.");
            }
        }

        UpdateAnsweringState(false);
        ChatReset?.Invoke();
        SessionStateChanged?.Invoke();
    }

    public async Task CancelAsync()
    {
        _cancellationTokenSource?.Cancel();
        _toolApprovalCoordinator?.CancelPending();
        Task? activeRun;
        lock (_lifecycleLock)
        {
            activeRun = _activeRunCompletion?.Task;
        }

        if (activeRun is not null)
        {
            await activeRun;
        }
    }

    public async Task SendAsync(
        string text,
        IReadOnlyList<AppChatMessageFile>? files = null,
        CancellationToken cancellationToken = default) =>
        await RunAsync(text, files ?? [], includeUserMessage: true, cancellationToken);

    public Task RunOnStartAsync(CancellationToken cancellationToken = default) =>
        RunAsync(null, [], includeUserMessage: false, cancellationToken);

    private async Task RunAsync(
        string? text,
        IReadOnlyList<AppChatMessageFile> files,
        bool includeUserMessage,
        CancellationToken cancellationToken)
    {
        if (_parameters is null)
        {
            throw new InvalidOperationException("Chat session not started.");
        }

        if ((includeUserMessage && string.IsNullOrWhiteSpace(text)) || IsAnswering || _resetting || PendingToolApproval is not null)
        {
            return;
        }

        if (RequiresReset)
        {
            throw new InvalidOperationException("This conversation cannot continue after a canceled or failed run. Start a new chat.");
        }

        if (_parameters.RuntimeReference is null)
        {
            throw new InvalidOperationException("Unified agent runtime reference is not configured.");
        }

        if (!includeUserMessage && _parameters.RuntimeReference.Kind != AgentDefinitionKind.SavedWorkflow)
        {
            throw new InvalidOperationException("Only workflows can run when a chat starts.");
        }

        if (!includeUserMessage && _parameters.LaunchBehavior != AgentLaunchBehavior.RunOnStart)
        {
            throw new InvalidOperationException("This workflow waits for a user message.");
        }

        await _runSetupGate.WaitAsync(cancellationToken);
        long generation;
        try
        {
            if (_resetting || IsAnswering)
            {
                return;
            }

            if (RequiresReset)
            {
                throw new InvalidOperationException("This conversation cannot continue after a canceled or failed run. Start a new chat.");
            }

            generation = Interlocked.Read(ref _generation);
            if (_parameters.RuntimeReference.Kind == AgentDefinitionKind.SavedAgent)
            {
                await EnsureDirectConversationAsync(cancellationToken);
            }

            if (generation != Interlocked.Read(ref _generation))
            {
                return;
            }

            if (includeUserMessage)
            {
                var userMessage = new AppChatMessage(text!, DateTime.Now, AppChatRole.User, files: files);
                await AddMessageAsync(userMessage);
            }
            _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _activeRunCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            UpdateAnsweringState(true);
        }
        finally
        {
            _runSetupGate.Release();
        }

        if (_parameters.RuntimeReference.Kind == AgentDefinitionKind.SavedAgent)
        {
            await SendDirectAsync(text!, files, generation, cancellationToken);
            return;
        }

        var terminalFailureHandled = false;

        try
        {
            var runtimeRequest = new AgentRuntimeRunRequest
            {
                InvocationKind = includeUserMessage
                    ? AgentRuntimeInvocationKind.UserMessage
                    : AgentRuntimeInvocationKind.RunOnStart,
                Messages = _chat.Messages
                    .Where(static message => !message.IsStreaming)
                    .Where(static message => message.Role is AppChatRole.System or AppChatRole.User or AppChatRole.Assistant)
                    .Select(static message => new AgentInputMessage(
                        message.Role switch
                        {
                            AppChatRole.System => AgentMessageRole.System,
                            AppChatRole.Assistant => AgentMessageRole.Assistant,
                            _ => AgentMessageRole.User
                        },
                        message.Content))
                    .ToList(),
                Inputs = new Dictionary<string, string>(
                    _parameters.RuntimeInputs,
                    StringComparer.OrdinalIgnoreCase),
                Attachments = files
                    .Select(ToAgentInputAttachment)
                    .ToList()
            };

            var creationContext = new AgentRuntimeCreationContext
            {
                Configuration = _parameters.Configuration,
                DefaultModel = _parameters.RuntimeDefaultModel ?? _parameters.Agents.FirstOrDefault()?.Model,
                Overrides = _parameters.Overrides,
                RuntimeResources = BuildRuntimeResources()
            };
            var descriptor = await definitionCatalog.GetRequiredAsync(
                _parameters.RuntimeReference,
                _cancellationTokenSource.Token);
            var runContext = runContextFactory.CreateRoot(
                descriptor,
                _chat.Id.ToString("N"));

            await foreach (var runEvent in agentRunner.RunAsync(
                               _parameters.RuntimeReference,
                               runtimeRequest,
                               creationContext,
                               runContext,
                               _cancellationTokenSource!.Token))
            {
                if (runEvent is AgentRunFailed)
                {
                    terminalFailureHandled = true;
                }

                if (generation != Interlocked.Read(ref _generation))
                {
                    break;
                }

                await ApplyRunEventAsync(runEvent, generation, _cancellationTokenSource.Token);
            }
        }
        catch (OperationCanceledException)
        {
            if (generation == Interlocked.Read(ref _generation))
            {
                RequiresReset = true;
                await CancelActiveStreamsAsync();
            }
        }
        catch (Exception ex)
        {
            if (generation == Interlocked.Read(ref _generation))
            {
                RequiresReset = true;
                logger.LogError(ex, "Unified agent chat run failed.");
                await CancelActiveStreamsAsync();
                if (!terminalFailureHandled)
                {
                    await AddMessageAsync(new AppChatMessage(
                        "Agent runtime error: The run failed before a terminal result was produced.",
                        DateTime.Now,
                        AppChatRole.Assistant));
                }
            }
        }
        finally
        {
            if (generation == Interlocked.Read(ref _generation))
            {
                await CheckpointCurrentAsync();
                ClearRunLocalState();
                _cancellationTokenSource?.Dispose();
                _cancellationTokenSource = null;
                UpdateAnsweringState(false);
            }

            _activeRunCompletion?.TrySetResult();
        }
    }

    public async Task RespondToToolApprovalAsync(
        ToolApprovalDecision decision,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(decision))
        {
            throw new ArgumentOutOfRangeException(nameof(decision));
        }

        await _runSetupGate.WaitAsync(cancellationToken);
        long generation;
        ToolApprovalRequestContent request;
        SessionToolApprovalRequest? coordinatorRequest;
        try
        {
            if (_resetting || IsAnswering || RequiresReset)
                throw new InvalidOperationException("The pending tool approval cannot be answered while this conversation is unavailable.");

            if (PendingToolApproval is { SessionScope: ToolApprovalSessionScope.None } &&
                decision == ToolApprovalDecision.ApproveForSession)
                throw new InvalidOperationException("Standing approval is not available for this tool.");

            coordinatorRequest = _toolApprovalCoordinator?.PendingRequest;
            if (coordinatorRequest is not null)
            {
                if (!_toolApprovalCoordinator!.TryRespond(coordinatorRequest.RequestId, decision, request =>
                    ApplySessionGrant(request.ToolName, request.RuntimeAgentId, decision)))
                {
                    throw new InvalidOperationException("The pending tool approval is no longer active.");
                }

                return;
            }

            request = _pendingToolApprovalRequest
                ?? throw new InvalidOperationException("There is no pending tool approval for this conversation.");
            if (_directAgent is null || _directSession is null)
                throw new InvalidOperationException("A direct agent session is not available.");

            generation = Interlocked.Read(ref _generation);
            _pendingToolApprovalRequest = null;
            PendingToolApproval = null;
            _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _activeRunCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            UpdateAnsweringState(true);
            SessionStateChanged?.Invoke();
        }
        finally
        {
            _runSetupGate.Release();
        }

        AIContent response = ApplyToolApprovalDecision(GetToolName(request), _directRuntimeAgentId, decision, request);

        await RunDirectAsync([new ChatMessage(ChatRole.User, [response])], generation);
    }

    public async Task RestoreSavedChatAsync(
        SavedChatDocument chat,
        CancellationToken cancellationToken = default,
        IProgress<ChatSessionRestoreProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(chat);
        var restoreStartedAt = Stopwatch.GetTimestamp();
        logger.LogInformation("Saved chat restore started. ChatId={ChatId}", chat.Id);
        progress?.Report(new ChatSessionRestoreProgress(
            ChatSessionRestoreStage.ValidatingSavedChat,
            "Validating saved chat..."));
        if (chat.FormatVersion > SavedChatDocument.CurrentFormatVersion)
            throw new InvalidOperationException("The saved chat format is newer than this OllamaChat version.");
        if (chat.FormatVersion != SavedChatDocument.CurrentFormatVersion || chat.Launch.RuntimeReference is null ||
            !Enum.TryParse<AgentDefinitionKind>(chat.Launch.RuntimeReference.Kind, out var kind))
            throw new InvalidOperationException("Saved chat file is invalid.");
        if (IsAnswering || PendingToolApproval is not null || RequiresReset)
            throw new InvalidOperationException("Finish or stop the current response before opening another chat.");

        if (kind == AgentDefinitionKind.SavedAgent && string.IsNullOrWhiteSpace(chat.NativeSession?.SnapshotJson))
            throw new InvalidOperationException("This saved chat does not contain a resumable Harness session.");
        chat.StorageRoot = Path.GetFullPath(chat.StorageRoot ?? throw new InvalidOperationException("Saved chat storage root is missing."));

        if (!await _startGate.WaitAsync(0, cancellationToken))
            throw new InvalidOperationException("Chat session startup is already in progress.");

        try
        {
            await _runSetupGate.WaitAsync(cancellationToken);
            try
            {
                if (_resetting || IsAnswering || PendingToolApproval is not null || RequiresReset)
                    throw new InvalidOperationException("Finish or stop the current response before opening another chat.");

                var prepared = await PrepareSavedChatRestoreAsync(chat, kind, cancellationToken, progress, restoreStartedAt);
                try
                {
                    progress?.Report(new ChatSessionRestoreProgress(
                        ChatSessionRestoreStage.RestoringConversation,
                        "Restoring conversation..."));
                    CommitSavedChatRestore(prepared);
                    logger.LogInformation("Saved chat restore completed. ChatId={ChatId}, ElapsedMs={ElapsedMs}", chat.Id, Stopwatch.GetElapsedTime(restoreStartedAt).TotalMilliseconds);
                }
                catch
                {
                    await prepared.DisposeAsync();
                    throw;
                }
            }
            finally { _runSetupGate.Release(); }
        }
        finally { _startGate.Release(); }
    }

    private async Task<PreparedSavedChatRestore> PrepareSavedChatRestoreAsync(
        SavedChatDocument chat,
        AgentDefinitionKind kind,
        CancellationToken cancellationToken,
        IProgress<ChatSessionRestoreProgress>? progress,
        long restoreStartedAt)
    {

        var reference = new AgentDefinitionReference(kind, chat.Launch.RuntimeReference.Id);
        var resolutionRequest = new AgentSessionDefinitionRequest
        {
            UiModelSelection = chat.Launch.Model is null ? new ServerModelSelection(null, null) : new ServerModelSelection(chat.Launch.Model.ServerId, chat.Launch.Model.ModelName),
            Inputs = new Dictionary<string, string>(chat.Launch.Inputs, StringComparer.OrdinalIgnoreCase),
            Overrides = new AgentSessionOverrides
            {
                WorkspacePath = chat.Launch.Overrides.WorkspacePath,
                SandboxProfileId = chat.Launch.Overrides.SandboxProfileId,
                McpServerBindings = chat.Launch.Overrides.McpServerBindings?.Select(static binding => binding.Clone()).ToList()
            }
        };
        var resolver = definitionResolver ?? throw new InvalidOperationException("Saved chat restore is unavailable because the definition resolver is not configured.");
        progress?.Report(new ChatSessionRestoreProgress(
            ChatSessionRestoreStage.ResolvingDefinition,
            "Resolving agent configuration..."));
        var validation = await resolver.ValidateAsync(reference, resolutionRequest, cancellationToken);
        if (!validation.CanLaunch)
            throw new InvalidOperationException(string.Join(" ", validation.Problems.Select(static problem => problem.Message)));
        var resolved = await resolver.ResolveAsync(reference, resolutionRequest, cancellationToken);
        if (!resolved.Validation.CanLaunch)
            throw new InvalidOperationException(string.Join(" ", resolved.Validation.Problems.Select(static problem => problem.Message)));
        logger.LogInformation("Saved chat definition resolved. ChatId={ChatId}, ElapsedMs={ElapsedMs}", chat.Id, Stopwatch.GetElapsedTime(restoreStartedAt).TotalMilliseconds);
        HarnessSessionSnapshot? nativeSnapshot = null;
        if (kind == AgentDefinitionKind.SavedAgent)
        {
            try
            {
                nativeSnapshot = JsonSerializer.Deserialize<HarnessSessionSnapshot>(chat.NativeSession.SnapshotJson)
                    ?? throw new JsonException("The snapshot is empty.");
            }
            catch (JsonException)
            {
                throw new InvalidOperationException("The saved chat contains an invalid Harness session snapshot.");
            }

            if (nativeSnapshot.FormatVersion != HarnessSessionSnapshot.CurrentFormatVersion)
                throw new InvalidOperationException($"Harness session snapshot format {nativeSnapshot.FormatVersion} is not supported.");
            ValidateSnapshotStructure(nativeSnapshot);
            if (!Guid.TryParse(reference.Id, out var savedAgentId) || nativeSnapshot.SavedAgentId != savedAgentId ||
                resolved.DefaultModel is null || nativeSnapshot.ModelServerId != resolved.DefaultModel.ServerId ||
                !string.Equals(nativeSnapshot.ModelName, resolved.DefaultModel.ModelName, StringComparison.Ordinal) ||
                !HaveEquivalentWorkspacePaths(resolutionRequest.Overrides.WorkspacePath, nativeSnapshot.Overrides.WorkspacePath) ||
                resolutionRequest.Overrides.SandboxProfileId != nativeSnapshot.Overrides.SandboxProfileId ||
                !HaveEquivalentMcpBindings(resolutionRequest.Overrides.McpServerBindings, nativeSnapshot.Overrides.McpServerBindings))
                throw new InvalidOperationException("The saved Harness session is incompatible with its saved launch configuration.");
            var template = await agentTemplateService.GetByIdAsync(savedAgentId)
                ?? throw new InvalidOperationException("The Saved Agent used by this chat was deleted.");
            if (template.UpdatedAt != nativeSnapshot.AgentUpdatedAt)
                throw new InvalidOperationException("The saved Harness session was created with an older agent configuration.");
        }
        var request = new ChatEngineSessionStartRequest
        {
            Configuration = new AppChatConfiguration(chat.Launch.Model?.ModelName ?? string.Empty, []),
            Agents = [],
            RuntimeReference = resolved.RuntimeReference,
            RuntimeDefaultModel = resolved.DefaultModel,
            LaunchBehavior = Enum.TryParse<AgentLaunchBehavior>(chat.Launch.LaunchBehavior, out var launchBehavior)
                ? launchBehavior
                : resolved.Descriptor.LaunchBehavior,
            RuntimeInputs = new Dictionary<string, string>(resolved.Inputs, StringComparer.OrdinalIgnoreCase),
            Overrides = resolutionRequest.Overrides,
            RuntimeParticipant = resolved.PresentationParticipant
        };
        var descriptor = await definitionCatalog.GetRequiredAsync(request.RuntimeReference!, cancellationToken);
        SandboxSessionHandle? sandbox = null;
        HarnessAgentRuntimeDefinition? runtime = null;
        ISessionToolApprovalCoordinator? coordinator = null;
        try
        {
            sandbox = await CreateSandboxSessionAsync(request, descriptor, Guid.NewGuid().ToString("N"), cancellationToken, CreateRestoreSandboxProgress(progress));
            if (sandbox is not null)
                logger.LogInformation("Saved chat sandbox prepared. ChatId={ChatId}, ElapsedMs={ElapsedMs}", chat.Id, Stopwatch.GetElapsedTime(restoreStartedAt).TotalMilliseconds);
            if (kind == AgentDefinitionKind.SavedAgent)
            {
                progress?.Report(new ChatSessionRestoreProgress(
                    ChatSessionRestoreStage.RestoringAgentSession,
                    "Restoring agent session..."));
                var policy = new SessionToolApprovalPolicy();
                policy.SetWorkspace(sandbox?.WorkspacePath ?? request.Overrides.WorkspacePath);
                coordinator = new SessionToolApprovalCoordinator();
                runtime = await CreateDirectRuntimeAsync(request, coordinator, policy, sandbox, cancellationToken);
                var session = await runtime.Agent.DeserializeSessionAsync(nativeSnapshot!.Session, cancellationToken: cancellationToken);
                logger.LogInformation("Saved chat agent session restored. ChatId={ChatId}, ElapsedMs={ElapsedMs}", chat.Id, Stopwatch.GetElapsedTime(restoreStartedAt).TotalMilliseconds);
                var template = await agentTemplateService.GetByIdAsync(nativeSnapshot.SavedAgentId)
                    ?? throw new InvalidOperationException("The Saved Agent used by this chat was deleted.");
                return new PreparedSavedChatRestore(request, chat, sandbox, runtime, session, coordinator, policy, template.AgentId);
            }

            return new PreparedSavedChatRestore(request, chat, sandbox, null, null, null, null, string.Empty);
        }
        catch (Exception ex)
        {
            runtime?.Dispose();
            if (sandbox is not null)
                await DisposeSandboxAfterRestoreFailureAsync(sandbox);
            logger.LogWarning(ex, "Saved chat restore preparation failed.");
            throw new InvalidOperationException("Could not restore the saved chat. The current conversation is unchanged.");
        }
    }

    private static IProgress<ChatSessionStartProgress>? CreateRestoreSandboxProgress(
        IProgress<ChatSessionRestoreProgress>? progress)
    {
        if (progress is null)
            return null;

        return new CallbackProgress<ChatSessionStartProgress>(value =>
        {
            var stage = value.Stage switch
            {
                ChatSessionStartStage.CheckingSandboxAvailability => ChatSessionRestoreStage.CheckingSandboxAvailability,
                ChatSessionStartStage.StartingSandbox => ChatSessionRestoreStage.StartingSandbox,
                ChatSessionStartStage.VerifyingSandbox => ChatSessionRestoreStage.VerifyingSandbox,
                _ => (ChatSessionRestoreStage?)null
            };
            if (stage is not null)
                progress.Report(new ChatSessionRestoreProgress(stage.Value, value.Message));
        });
    }

    private sealed class CallbackProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private void CommitSavedChatRestore(PreparedSavedChatRestore prepared)
    {
        var previousRuntime = _directRuntimeDefinition;
        var previousCoordinator = _toolApprovalCoordinator;
        var previousSandbox = _sandboxSession;
        previousCoordinator?.PendingRequestChanged -= HandleCoordinatorPendingRequestChanged;

        _parameters = prepared.Request.Snapshot();
        _activeSession = CreateActiveSession(_parameters);
        _directRuntimeDefinition = prepared.Runtime;
        _directAgent = prepared.Runtime?.Agent;
        _directSession = prepared.Session;
        _directRuntimeAgentId = prepared.RuntimeAgentId;
        _directAvailableModes = prepared.Runtime?.AvailableModes ?? [];
        _directFileAccessStore = prepared.Runtime?.FileAccessStore;
        _directFileAccessProfile = prepared.Runtime?.FileAccessProfile;
        _directFileMemoryStore = prepared.Runtime?.FileMemoryStore;
        _directCompaction = prepared.Runtime?.Compaction;
        _directSkills = prepared.Runtime?.Skills ?? [];
        _directSkillDiagnostics = prepared.Runtime?.SkillDiagnostics ?? [];
        _directBackgroundAgents = prepared.Runtime?.BackgroundAgents ?? [];
        _directToolMetadata = prepared.Runtime?.ToolSet.MetadataByName ?? new Dictionary<string, AgenticRegisteredTool>(StringComparer.OrdinalIgnoreCase);
        _toolApprovalCoordinator = prepared.Coordinator;
        _toolApprovalPolicy = prepared.Policy;
        if (_toolApprovalCoordinator is not null)
            _toolApprovalCoordinator.PendingRequestChanged += HandleCoordinatorPendingRequestChanged;
        _sandboxSession = prepared.Sandbox;
        _savedChat = new SavedChatHandle(prepared.Chat.Id, prepared.Chat.StorageRoot!);
        _savedChatPersistenceSuppressed = false;
        prepared.TransferOwnership();

        _chat.Reset();
        _chat.SetAgents(_parameters.RuntimeParticipant is { } participant
            ? [new AgentExecutionSpec { RuntimeAgentId = participant.Id, AgentName = participant.Name, Summary = participant.Description, ShortName = participant.AvatarText }]
            : _parameters.Agents.Select(static agent => agent.Agent.Clone()));
        foreach (var message in prepared.Chat.Messages)
            _chat.Messages.Add(new AppChatMessage(message));
        ClearRunLocalState();
        RequiresReset = false;
        _persistenceHealthy = true;
        harnessTraceSession?.Clear();

        try
        { previousRuntime?.Dispose(); }
        catch (Exception ex) { logger.LogWarning(ex, "Could not dispose the previous Harness runtime after saved chat restore."); }
        if (previousSandbox is not null)
            _ = DisposeSandboxAfterRestoreFailureAsync(previousSandbox);
        ChatReset?.Invoke();
        SessionStateChanged?.Invoke();
    }

    private async Task CreateDirectConversationAsync(
        ChatEngineSessionStartRequest request,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(request.RuntimeReference!.Id, out var templateId) ||
            request.RuntimeDefaultModel is null)
        {
            throw new InvalidOperationException("The saved agent and model must be resolved before starting a conversation.");
        }

        var build = await CreateDirectRuntimeAsync(request, _toolApprovalCoordinator!, _toolApprovalPolicy!, _sandboxSession, cancellationToken);

        try
        {
            _directSession = await build.Agent.CreateSessionAsync(cancellationToken);
        }
        catch
        {
            build.Dispose();
            throw;
        }

        _directRuntimeDefinition = build;
        _directAgent = build.Agent;
        _directRuntimeAgentId = (await agentTemplateService.GetByIdAsync(templateId))!.AgentId;
        _directAvailableModes = build.AvailableModes;
        _directFileAccessStore = build.FileAccessStore;
        _directFileAccessProfile = build.FileAccessProfile;
        _directFileMemoryStore = build.FileMemoryStore;
        _directCompaction = build.Compaction;
        _directSkills = build.Skills;
        _directSkillDiagnostics = build.SkillDiagnostics;
        _directBackgroundAgents = build.BackgroundAgents;
        _directToolMetadata = build.ToolSet.MetadataByName;
        SessionStateChanged?.Invoke();
    }

    private async Task<HarnessAgentRuntimeDefinition> CreateDirectRuntimeAsync(
        ChatEngineSessionStartRequest request,
        ISessionToolApprovalCoordinator approvalCoordinator,
        SessionToolApprovalPolicy approvalPolicy,
        SandboxSessionHandle? sandboxSession,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(request.RuntimeReference!.Id, out var templateId) || request.RuntimeDefaultModel is null)
            throw new InvalidOperationException("The saved agent and model must be resolved before starting a conversation.");

        var template = await agentTemplateService.GetByIdAsync(templateId)
            ?? throw new InvalidOperationException($"Saved agent '{request.RuntimeReference.Id}' was not found.");
        if (request.Overrides.McpServerBindings is not null)
        {
            template = template.Clone();
            template.McpServerBindings = request.Overrides.McpServerBindings
                .Select(static binding => binding.Clone())
                .ToList();
        }

        var resolved = ResolvedChatAgentFactory.Resolve(template, request.RuntimeDefaultModel);
        var build = await runtimeAgentFactory.CreateAsync(new AgentRunRequest
        {
            Agent = resolved.Agent,
            ResolvedModel = resolved.Model,
            Configuration = request.Configuration,
            Conversation = [],
            UserMessage = string.Empty,
            RuntimeResources = BuildRuntimeResources(approvalCoordinator, approvalPolicy, sandboxSession)
        }, cancellationToken: cancellationToken);
        return build;
    }

    private AgentSessionRuntimeResources BuildRuntimeResources() =>
        BuildRuntimeResources(_toolApprovalCoordinator!, _toolApprovalPolicy!, _sandboxSession);

    private AgentSessionRuntimeResources BuildRuntimeResources(
        ISessionToolApprovalCoordinator approvalCoordinator,
        SessionToolApprovalPolicy approvalPolicy,
        SandboxSessionHandle? sandboxSession) => new()
        {
            WorkspacePath = sandboxSession?.WorkspacePath ?? _parameters?.Overrides.WorkspacePath,
            Sandbox = sandboxSession?.Instance,
            ToolApprovalCoordinator = approvalCoordinator,
            ToolApprovalPolicy = approvalPolicy
        };

    private async Task<SandboxSessionHandle?> CreateSandboxSessionAsync(
        ChatEngineSessionStartRequest request,
        AgentDefinitionDescriptor descriptor,
        string sessionId,
        CancellationToken cancellationToken,
        IProgress<ChatSessionStartProgress>? progress)
    {
        if (!descriptor.LaunchCapabilities.SupportsSandboxProfile)
        {
            return null;
        }

        if (request.Overrides.SandboxProfileId is not Guid sandboxProfileId || sandboxProfileId == Guid.Empty)
        {
            throw new InvalidOperationException("A sandbox profile is required to start this session.");
        }

        if (string.IsNullOrWhiteSpace(request.Overrides.WorkspacePath))
        {
            throw new InvalidOperationException("A workspace directory is required to start this session.");
        }

        return await sandboxSessionFactory.StartAsync(
            sandboxProfileId,
            request.Overrides.WorkspacePath,
            sessionId,
            cancellationToken,
            progress);
    }

    public async ValueTask DisposeAsync()
    {
        await ResetAsync();
        _startGate.Dispose();
        _runSetupGate.Dispose();
    }

    private async Task EnsureDirectConversationAsync(CancellationToken cancellationToken)
    {
        if (_parameters is null)
        {
            throw new InvalidOperationException("Chat session not started.");
        }

        if (_directAgent is not null && _directSession is not null)
        {
            return;
        }

        await CreateDirectConversationAsync(_parameters, cancellationToken);
    }

    private async Task SendDirectAsync(
        string text,
        IReadOnlyList<AppChatMessageFile> files,
        long generation,
        CancellationToken cancellationToken)
    {
        if (generation != Interlocked.Read(ref _generation))
        {
            return;
        }

        await RunDirectAsync([BuildDirectUserMessage(text, files)], generation);
    }

    private async Task RunDirectAsync(IReadOnlyList<ChatMessage> input, long generation)
    {
        var messageId = $"direct-harness-response-{Guid.NewGuid():N}";
        StreamingAppChatMessage? stream = null;
        var projection = responseEventProjector.CreateProjection();
        using var ragTurn = _directRuntimeDefinition?.RagRetrievalTraceSink?.BeginTurn(messageId);
        HarnessTraceSession.HarnessTraceRunScope? traceRun = harnessTraceSession?.TryBeginRun(messageId);

        try
        {
            await foreach (var update in _directAgent!.RunStreamingAsync(
                               input,
                               _directSession,
                               BuildDirectRunOptions(),
                               _cancellationTokenSource.Token))
            {
                if (generation != Interlocked.Read(ref _generation))
                {
                    break;
                }

                var traces = _directRuntimeDefinition?.RagRetrievalTraceSink?.Drain(messageId) ?? [];
                if (traces.Count > 0)
                {
                    stream ??= await GetOrCreateStreamAsync(messageId, _chat.Agents.FirstOrDefault()?.AgentName ?? "Agent");
                    foreach (var trace in traces)
                        stream.AddOrUpdateRagRetrieval(trace);
                    await (MessageUpdated?.Invoke(stream, false) ?? Task.CompletedTask);
                }

                foreach (var responseEvent in projection.Project(update, _directToolMetadata))
                {
                    if (generation != Interlocked.Read(ref _generation))
                    {
                        break;
                    }

                    if (responseEvent is HarnessToolApprovalRequested approval)
                    {
                        var approvalRequest = update.Contents.OfType<ToolApprovalRequestContent>()
                            .FirstOrDefault(content => content.RequestId == approval.RequestId);
                        if (approvalRequest is not null)
                        {
                            _pendingToolApprovalRequest = approvalRequest;
                            PendingToolApproval = new ToolApprovalRequestViewModel(
                                approval.RequestId, approval.ToolName, approval.Arguments,
                                GetSessionScope(approval.ToolName),
                                GetApprovalWorkspace(approval.ToolName));
                            SessionStateChanged?.Invoke();
                        }

                        continue;
                    }

                    stream ??= await GetOrCreateStreamAsync(
                        messageId,
                        _chat.Agents.FirstOrDefault()?.AgentName ?? "Agent");
                    ApplyHarnessEvent(stream, responseEvent);

                    if (responseEvent is HarnessToolCallCompleted completed &&
                        ChangesHarnessSessionState(completed))
                    {
                        SessionStateChanged?.Invoke();
                    }

                    await (MessageUpdated?.Invoke(stream, false) ?? Task.CompletedTask);
                }
            }

            if (generation != Interlocked.Read(ref _generation))
            {
                return;
            }

            var finalTraces = _directRuntimeDefinition?.RagRetrievalTraceSink?.Drain(messageId) ?? [];
            if (finalTraces.Count > 0)
            {
                stream ??= await GetOrCreateStreamAsync(messageId, _chat.Agents.FirstOrDefault()?.AgentName ?? "Agent");
                foreach (var trace in finalTraces)
                    stream.AddOrUpdateRagRetrieval(trace);
            }

            if (stream is not null)
            {
                traceRun?.Dispose();
                traceRun = null;
                var usage = harnessTraceSession?.GetUsage(messageId, runUsageAggregator);
                var final = streamingBridge.Complete(stream, stream.Content, null, usage);
                ReplaceMessage(stream, final);
                await (MessageUpdated?.Invoke(final, true) ?? Task.CompletedTask);
                _activeStreamsByRuntimeMessageId.Remove(messageId);
            }
            SessionStateChanged?.Invoke();
        }
        catch (OperationCanceledException)
        {
            traceRun?.Cancel();
            if (generation == Interlocked.Read(ref _generation))
            {
                RequiresReset = true;
                _directSession = null;
                await CancelActiveStreamsAsync();
            }
        }
        catch (Exception ex)
        {
            traceRun?.Fail();
            if (generation == Interlocked.Read(ref _generation))
            {
                RequiresReset = true;
                _directSession = null;
                logger.LogError(ex, "Harness direct chat run failed.");
                await CancelActiveStreamsAsync();
                await AddMessageAsync(new AppChatMessage($"Agent runtime error: {ex.Message}", DateTime.Now, AppChatRole.Assistant));
            }
        }
        finally
        {
            traceRun?.Dispose();
            if (generation == Interlocked.Read(ref _generation))
            {
                await CheckpointCurrentAsync();
                _cancellationTokenSource?.Dispose();
                _cancellationTokenSource = null;
                UpdateAnsweringState(false);
            }

            _activeRunCompletion?.TrySetResult();
        }
    }

    private ChatClientAgentRunOptions BuildDirectRunOptions()
    {
        if (_parameters?.RuntimeDefaultModel is null)
        {
            return new ChatClientAgentRunOptions(new ChatOptions());
        }

        var agent = _chat.Agents.FirstOrDefault();
        var options = new ChatOptions
        {
            ModelId = _parameters.RuntimeDefaultModel.ModelName,
            Temperature = agent?.Temperature is double temperature
                ? (float)temperature
                : null
        };

        if (agent?.RepeatPenalty is double repeatPenalty)
        {
            options.AdditionalProperties ??= [];
            options.AdditionalProperties["repeat_penalty"] = repeatPenalty;
        }

        return new ChatClientAgentRunOptions(options);
    }

    private static ChatMessage BuildDirectUserMessage(
        string text,
        IReadOnlyList<AppChatMessageFile> files)
    {
        if (files.Count == 0)
        {
            return new ChatMessage(ChatRole.User, text);
        }

        List<AIContent> contents = [new TextContent(text)];
        foreach (var file in files)
        {
            if (IsTextAttachment(file))
            {
                contents.Add(new TextContent(Encoding.UTF8.GetString(file.Data)));
                continue;
            }

            contents.Add(new DataContent(file.Data, file.ContentType));
        }

        return new ChatMessage(ChatRole.User, contents);
    }

    private async Task ApplyRunEventAsync(
        AgentRunEvent runEvent,
        long generation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (generation != Interlocked.Read(ref _generation))
        {
            return;
        }

        switch (runEvent)
        {
            case AgentTextDelta delta:
                var stream = await GetOrCreateStreamAsync(delta.MessageId, delta.Author);
                streamingBridge.Append(stream, delta.Text);
                await (MessageUpdated?.Invoke(stream, false) ?? Task.CompletedTask);
                break;

            case AgentToolCallStarted started:
                await ApplyToolInvocationAsync(started.MessageId, started.Author, started.Invocation);
                break;

            case AgentToolCallCompleted completed:
                await ApplyToolInvocationAsync(completed.MessageId, completed.Author, completed.Invocation);
                break;

            case AgentToolCallFailed failed:
                await ApplyToolInvocationAsync(failed.MessageId, failed.Author, failed.Invocation);
                break;
            case AgentRunRagRetrievalCompleted retrieval:
                var ragStream = await GetOrCreateStreamAsync(retrieval.MessageId, retrieval.Author);
                ragStream.AddOrUpdateRagRetrieval(retrieval.Trace);
                await (MessageUpdated?.Invoke(ragStream, false) ?? Task.CompletedTask);
                break;

            case AgentMessageCompleted completed:
                await CompleteOrAddAssistantMessageAsync(completed.MessageId, completed.Message);
                break;

            case AgentRunCompleted completed:
                await ApplyFinalResultAsync(completed.Result);
                await CompleteRemainingStreamsAsync();
                break;

            case AgentRunFailed failed:
                await AddFailureAsync(failed.Error);
                break;
        }
    }

    private async Task CompleteOrAddAssistantMessageAsync(
        string runtimeMessageId,
        AgentOutputMessage output)
    {
        var agentId = ResolveOutputAgentId(output);
        if (_activeStreamsByRuntimeMessageId.TryGetValue(runtimeMessageId, out var stream))
        {
            if (!string.IsNullOrWhiteSpace(agentId))
            {
                stream.SetAgentId(agentId);
            }
            if (!string.IsNullOrWhiteSpace(output.Author))
            {
                stream.SetAgentName(output.Author);
            }

            var final = streamingBridge.Complete(stream, output.Content);
            ReplaceMessage(stream, final);
            await (MessageUpdated?.Invoke(final, true) ?? Task.CompletedTask);
            _activeStreamsByRuntimeMessageId.Remove(runtimeMessageId);
            _completedRuntimeMessageIds.Add(runtimeMessageId);
            return;
        }

        await AddMessageAsync(new AppChatMessage(
            output.Content,
            DateTime.Now,
            AppChatRole.Assistant,
            agentId: agentId,
            agentName: output.Author));
        _completedRuntimeMessageIds.Add(runtimeMessageId);
    }

    private async Task ApplyToolInvocationAsync(
        string runtimeMessageId,
        string author,
        ToolInvocationViewState invocation)
    {
        var stream = await GetOrCreateStreamAsync(runtimeMessageId, author);
        stream.UpdateToolInvocation(invocation);
        await (MessageUpdated?.Invoke(stream, true) ?? Task.CompletedTask);
    }

    private static void ApplyHarnessEvent(
        StreamingAppChatMessage stream,
        HarnessResponseEvent responseEvent)
    {
        switch (responseEvent)
        {
            case HarnessTextDelta text:
                stream.Append(text.Text);
                break;

            case HarnessToolCallStarted started:
                stream.StartToolInvocation(ToViewState(started));
                break;

            case HarnessToolCallCompleted completed:
                stream.UpdateToolInvocation(ToViewState(completed));
                break;

            case HarnessToolCallFailed failed:
                stream.UpdateToolInvocation(ToViewState(failed));
                break;
        }
    }

    internal static bool ChangesHarnessSessionState(HarnessToolCallCompleted completed)
    {
        ArgumentNullException.ThrowIfNull(completed);

        return IsHarnessSessionStateTool(completed.RegisteredName) ||
               IsHarnessSessionStateTool(completed.OriginalName);
    }

    private static bool IsHarnessSessionStateTool(string toolName) => toolName is
        "todos_add" or
        "todos_complete" or
        "todos_remove" or
        "mode_set";

    private async Task<AgentSessionFileMemoryViewModel?> GetFileMemoryStateAsync(
        AIAgent? agent,
        AgentSession? session,
        CancellationToken cancellationToken)
    {
        if (agent is null || session is null)
            return null;

        if (_directFileMemoryStore is null)
            return new AgentSessionFileMemoryViewModel(false, null, []);

        if (agent.GetService<FileMemoryProvider>() is not { } provider)
            return new AgentSessionFileMemoryViewModel(true, null, []);

        var stateKey = provider.StateKeys.SingleOrDefault();
        if (string.IsNullOrWhiteSpace(stateKey) || !session.StateBag.TryGetValue(stateKey, out FileMemoryState? state))
            return new AgentSessionFileMemoryViewModel(true, null, []);

        var entries = await _directFileMemoryStore.ListChildrenAsync(state.WorkingFolder, cancellationToken);
        var entryNames = entries.Where(entry => entry.Type == FileStoreEntry.File).Select(entry => entry.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var files = new List<AgentSessionFileMemoryEntryViewModel>();
        foreach (var entry in entries.Where(entry => entry.Type == FileStoreEntry.File && !IsInternalMemoryFile(entry.Name)))
        {
            var descriptionFileName = GetDescriptionFileName(entry.Name);
            var description = entryNames.Contains(descriptionFileName)
                ? await _directFileMemoryStore.ReadAsync(CombineMemoryPath(state.WorkingFolder, descriptionFileName), cancellationToken)
                : null;
            files.Add(new AgentSessionFileMemoryEntryViewModel(entry.Name, description));
        }
        return new AgentSessionFileMemoryViewModel(true, state.WorkingFolder, files);
    }

    private static string CombineMemoryPath(string workingFolder, string name) =>
        string.IsNullOrWhiteSpace(workingFolder) ? name : $"{workingFolder.TrimEnd('/')}/{name}";

    private sealed record SavedChatHandle(Guid Id, string StorageRoot);

    private sealed class PreparedSavedChatRestore(
        ChatEngineSessionStartRequest request,
        SavedChatDocument chat,
        SandboxSessionHandle? sandbox,
        HarnessAgentRuntimeDefinition? runtime,
        AgentSession? session,
        ISessionToolApprovalCoordinator? coordinator,
        SessionToolApprovalPolicy? policy,
        string runtimeAgentId) : IAsyncDisposable
    {
        public ChatEngineSessionStartRequest Request { get; } = request;
        public SavedChatDocument Chat { get; } = chat;
        public SandboxSessionHandle? Sandbox { get; private set; } = sandbox;
        public HarnessAgentRuntimeDefinition? Runtime { get; private set; } = runtime;
        public AgentSession? Session { get; } = session;
        public ISessionToolApprovalCoordinator? Coordinator { get; } = coordinator;
        public SessionToolApprovalPolicy? Policy { get; } = policy;
        public string RuntimeAgentId { get; } = runtimeAgentId;

        public void TransferOwnership()
        {
            Runtime = null;
            Sandbox = null;
        }

        public async ValueTask DisposeAsync()
        {
            Runtime?.Dispose();
            if (Sandbox is not null)
                await Sandbox.DisposeAsync();
        }
    }

    private static bool IsInternalMemoryFile(string name) =>
        name.Equals("memories.md", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith("_description.md", StringComparison.OrdinalIgnoreCase);

    internal static string GetDescriptionFileName(string memoryFileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(memoryFileName);
        var extensionIndex = memoryFileName.LastIndexOf('.');
        var baseName = extensionIndex > 0 ? memoryFileName[..extensionIndex] : memoryFileName;
        return $"{baseName}_description.md";
    }

    internal static bool HaveSameWorkspace(string fileAccessWorkspace, string sandboxWorkspace)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        return string.Equals(NormalizeWorkspacePath(fileAccessWorkspace), NormalizeWorkspacePath(sandboxWorkspace), comparison);
    }

    internal static bool HaveEquivalentWorkspacePaths(string? left, string? right)
    {
        if (left is null || right is null)
            return left is null && right is null;

        return HaveSameWorkspace(left, right);
    }

    private static string NormalizeWorkspacePath(string workspacePath)
    {
        try
        {
            // Normalize separators, resolve to absolute path and trim trailing separator
            var sep = Path.DirectorySeparatorChar;
            var alt = Path.AltDirectorySeparatorChar;
            var normalizedInput = workspacePath.Replace('\\', sep).Replace(alt, sep);

            var full = Path.GetFullPath(normalizedInput);
            return Path.TrimEndingDirectorySeparator(full);
        }
        catch (Exception) when (!string.IsNullOrWhiteSpace(workspacePath))
        {
            var sep = Path.DirectorySeparatorChar;
            var alt = Path.AltDirectorySeparatorChar;
            return workspacePath.TrimEnd(sep, alt);
        }
    }

    private static ToolInvocationViewState ToViewState(HarnessToolCallStarted value) => new(
        value.CallId, value.RegisteredName, value.OriginalName, value.Source, value.ServerName,
        value.BindingName, value.IsInteractive, value.Arguments, null, null,
        ToolInvocationStatus.Running, value.StartedAt, null);

    private static ToolInvocationViewState ToViewState(HarnessToolCallCompleted value) => new(
        value.CallId, value.RegisteredName, value.OriginalName, value.Source, value.ServerName,
        value.BindingName, value.IsInteractive, value.Arguments, value.Result, null,
        ToolInvocationStatus.Succeeded, value.StartedAt, value.CompletedAt);

    private static ToolInvocationViewState ToViewState(HarnessToolCallFailed value) => new(
        value.CallId, value.RegisteredName, value.OriginalName, value.Source, value.ServerName,
        value.BindingName, value.IsInteractive, value.Arguments, null, value.Error,
        ToolInvocationStatus.Failed, value.StartedAt, value.CompletedAt);

    private async Task ApplyFinalResultAsync(AgentRunResult result)
    {
        if (!string.IsNullOrWhiteSpace(result.FinalMessageId) &&
            _completedRuntimeMessageIds.Contains(result.FinalMessageId))
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(result.FinalMessageId))
        {
            await CompleteOrAddAssistantMessageAsync(result.FinalMessageId, result.FinalMessage);
            return;
        }

        await AddMessageAsync(new AppChatMessage(
            result.FinalMessage.Content,
            DateTime.Now,
            AppChatRole.Assistant,
            agentId: ResolveOutputAgentId(result.FinalMessage),
            agentName: result.FinalMessage.Author));
    }

    private static string? ResolveOutputAgentId(AgentOutputMessage output) =>
        !string.IsNullOrWhiteSpace(output.AgentId)
            ? output.AgentId
            : !string.IsNullOrWhiteSpace(output.Author)
                ? output.Author
                : null;

    private async Task AddFailureAsync(AgentRunError error)
    {
        if (error.Exception is not null)
        {
            logger.LogError(error.Exception, "Agent runtime failed with code {ErrorCode}.", error.Code);
        }
        else
        {
            logger.LogWarning(
                "Agent runtime failed with code {ErrorCode}: {ErrorMessage}",
                error.Code,
                error.Message);
        }
        await CancelActiveStreamsAsync();
        await AddMessageAsync(new AppChatMessage(
            $"Agent runtime error: {error.Message}",
            DateTime.Now,
            AppChatRole.Assistant));
    }

    private async Task<StreamingAppChatMessage> GetOrCreateStreamAsync(
        string runtimeMessageId,
        string author)
    {
        if (_activeStreamsByRuntimeMessageId.TryGetValue(runtimeMessageId, out var existing))
        {
            if (!string.IsNullOrWhiteSpace(author))
            {
                existing.SetAgentId(author);
                existing.SetAgentName(author);
            }

            return existing;
        }

        var stream = streamingBridge.Create(author, author);
        _activeStreamsByRuntimeMessageId[runtimeMessageId] = stream;
        await AddMessageAsync(stream);
        return stream;
    }

    private async Task CompleteRemainingStreamsAsync()
    {
        foreach (var pair in _activeStreamsByRuntimeMessageId.ToList())
        {
            if (string.IsNullOrWhiteSpace(pair.Value.Content) && pair.Value.ToolInvocations.Count == 0)
            {
                _activeStreamsByRuntimeMessageId.Remove(pair.Key);
                continue;
            }

            var final = streamingBridge.Complete(pair.Value);
            ReplaceMessage(pair.Value, final);
            await (MessageUpdated?.Invoke(final, true) ?? Task.CompletedTask);
            _completedRuntimeMessageIds.Add(pair.Key);
            _activeStreamsByRuntimeMessageId.Remove(pair.Key);
        }
    }

    private async Task CancelActiveStreamsAsync()
    {
        foreach (var stream in _activeStreamsByRuntimeMessageId.Values.ToList())
        {
            foreach (var invocation in stream.ToolInvocations
                         .Where(static invocation => invocation.Status == ToolInvocationStatus.Running)
                         .ToList())
            {
                stream.UpdateToolInvocation(invocation with
                {
                    Status = ToolInvocationStatus.Canceled,
                    Error = "Canceled",
                    CompletedAt = DateTimeOffset.UtcNow
                });
            }

            var canceled = streamingBridge.Cancel(stream);
            ReplaceMessage(stream, canceled);
            await (MessageUpdated?.Invoke(canceled, true) ?? Task.CompletedTask);
        }

        _activeStreamsByRuntimeMessageId.Clear();
    }

    private void ClearRunLocalState()
    {
        _activeStreamsByRuntimeMessageId.Clear();
        _completedRuntimeMessageIds.Clear();
    }

    private async Task CheckpointCurrentAsync()
    {
        if (_savedChatPersistenceSuppressed || savedChatService is null || chatTitleGenerator is null || RequiresReset || PendingToolApproval is not null || _parameters?.RuntimeReference is null ||
            _chat.Messages.Any(static message => message.IsStreaming) || !await savedChatService.IsAutoSaveEnabledAsync())
            return;

        try
        {
            var handle = _savedChat;
            var existing = handle is { }
                ? await savedChatService.GetAsync(handle.StorageRoot, handle.Id)
                : null;
            if (handle is not null && existing is null)
            {
                _savedChat = null;
                _savedChatPersistenceSuppressed = true;
                return;
            }
            var firstUserMessage = _chat.Messages.FirstOrDefault(static message => message.Role == AppChatRole.User)?.Content ?? string.Empty;
            var now = DateTime.UtcNow;
            var nativeSession = _parameters.RuntimeReference.Kind == AgentDefinitionKind.SavedAgent
                ? new SavedChatNativeSession { SnapshotJson = await CreateHarnessSnapshotAsync() }
                : null;
            var document = new SavedChatDocument
            {
                Id = handle?.Id ?? existing?.Id ?? Guid.NewGuid(),
                CreatedAtUtc = existing?.CreatedAtUtc ?? now,
                UpdatedAtUtc = now,
                Title = existing?.Title ?? chatTitleGenerator.Generate(firstUserMessage),
                IsTitleManual = existing?.IsTitleManual ?? false,
                Launch = new SavedChatLaunchSnapshot
                {
                    RuntimeReference = new SavedChatRuntimeReference(_parameters.RuntimeReference.Kind.ToString(), _parameters.RuntimeReference.Id),
                    AgentName = _chat.Agents.FirstOrDefault()?.AgentName ?? throw new InvalidOperationException("The saved chat agent name is unavailable."),
                    Model = _parameters.RuntimeDefaultModel,
                    LaunchBehavior = _parameters.LaunchBehavior.ToString(),
                    Inputs = new Dictionary<string, string>(_parameters.RuntimeInputs, StringComparer.OrdinalIgnoreCase),
                    Overrides = new SavedChatOverrides
                    {
                        WorkspacePath = _parameters.Overrides.WorkspacePath,
                        SandboxProfileId = _parameters.Overrides.SandboxProfileId,
                        McpServerBindings = _parameters.Overrides.McpServerBindings?.Select(static binding => binding.Clone()).ToList()
                    }
                },
                Messages = _chat.Messages.Where(static message => !message.IsStreaming).Select(static message => new AppChatMessage(message)).ToList(),
                NativeSession = nativeSession,
                StorageRoot = handle?.StorageRoot ?? existing?.StorageRoot
            };
            var checkpointed = handle is null
                ? await SaveNewCheckpointAsync(document)
                : await savedChatService.UpdateCheckpointAsync(document);
            if (!checkpointed)
            {
                _savedChat = null;
                _savedChatPersistenceSuppressed = true;
                return;
            }
            _savedChat = new SavedChatHandle(document.Id, document.StorageRoot ?? throw new InvalidOperationException("Saved chat storage root is missing."));
            _persistenceHealthy = true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not checkpoint the current saved chat.");
            if (_persistenceHealthy)
            {
                _persistenceHealthy = false;
                snackbar?.Add("The response completed, but this chat could not be saved.", Severity.Warning);
            }
        }
    }

    private async Task<bool> SaveNewCheckpointAsync(SavedChatDocument document)
    {
        await savedChatService!.SaveCheckpointAsync(document);
        return true;
    }

    private async Task<string> CreateHarnessSnapshotAsync(CancellationToken cancellationToken = default)
    {
        if (_directAgent is null || _directSession is null || _parameters?.RuntimeReference?.Kind != AgentDefinitionKind.SavedAgent ||
            !Guid.TryParse(_parameters.RuntimeReference.Id, out var agentId) || _parameters.RuntimeDefaultModel is null)
            throw new InvalidOperationException("A stable direct Harness session is required to export a session.");
        EnsureNoIncompleteBackgroundTasks(_directAgent, _directSession, "saved");
        var template = await agentTemplateService.GetByIdAsync(agentId) ?? throw new InvalidOperationException("The saved agent used by this session no longer exists.");
        var state = await _directAgent.SerializeSessionAsync(_directSession, cancellationToken: cancellationToken);
        return JsonSerializer.Serialize(new HarnessSessionSnapshot
        {
            SavedAgentId = agentId,
            AgentName = template.AgentName,
            AgentUpdatedAt = template.UpdatedAt,
            ModelServerId = _parameters.RuntimeDefaultModel.ServerId,
            ModelName = _parameters.RuntimeDefaultModel.ModelName,
            CreatedAtUtc = DateTime.UtcNow,
            Overrides = SnapshotOverrides(_parameters.Overrides),
            Session = state
        });
    }

    private async Task AddMessageAsync(IAppChatMessage message)
    {
        if (_chat.Messages.Any(existing => existing.Id == message.Id))
        {
            return;
        }

        _chat.Messages.Add(message);
        await (MessageAdded?.Invoke(message) ?? Task.CompletedTask);
    }

    private void ReplaceMessage(
        IAppChatMessage source,
        IAppChatMessage replacement)
    {
        var index = _chat.Messages.IndexOf(source);
        if (index >= 0)
        {
            _chat.Messages[index] = replacement;
            return;
        }

        _chat.Messages.Add(replacement);
    }

    private void UpdateAnsweringState(bool isAnswering)
    {
        IsAnswering = isAnswering;
        AnsweringStateChanged?.Invoke(isAnswering);
    }

    private void HandleCoordinatorPendingRequestChanged()
    {
        var pendingRequest = _toolApprovalCoordinator?.PendingRequest;
        PendingToolApproval = pendingRequest is null
            ? null
            : new ToolApprovalRequestViewModel(
                pendingRequest.RequestId,
                pendingRequest.ToolName,
                pendingRequest.Arguments,
                pendingRequest.SessionScope,
                pendingRequest.WorkspacePath);

        if (pendingRequest is not null)
        {
            UpdateAnsweringState(false);
        }
        else if (_activeRunCompletion is not null && _cancellationTokenSource is not null)
        {
            UpdateAnsweringState(true);
        }

        SessionStateChanged?.Invoke();
    }

    private static readonly ToolApprovalScopeResolver ToolApprovalScopeResolver = new();

    private ToolApprovalSessionScope GetSessionScope(string toolName) =>
        ToolApprovalScopeResolver.GetScope(toolName);

    private string? GetApprovalWorkspace(string toolName) => ToolApprovalScopeResolver.GetWorkspace(
        GetSessionScope(toolName), _directFileAccessStore?.WorkspacePath, _sandboxSession?.WorkspacePath);

    private AIContent ApplyToolApprovalDecision(
        string toolName,
        string runtimeAgentId,
        ToolApprovalDecision decision,
        ToolApprovalRequestContent? request = null)
    {
        var policy = _toolApprovalPolicy ?? throw new InvalidOperationException("Tool approval policy is unavailable.");
        return ToolApprovalDecisionApplier.Apply(
            request ?? throw new ArgumentNullException(nameof(request)), toolName, runtimeAgentId, decision, policy);
    }

    private void ApplySessionGrant(string toolName, string runtimeAgentId, ToolApprovalDecision decision)
    {
        if (decision == ToolApprovalDecision.ApproveForSession)
            (_toolApprovalPolicy ?? throw new InvalidOperationException("Tool approval policy is unavailable.")).Grant(toolName, runtimeAgentId);
    }

    private static string GetToolName(ToolApprovalRequestContent request) => request.ToolCall switch
    {
        FunctionCallContent functionCall => functionCall.Name,
        McpServerToolCallContent mcpCall => mcpCall.Name,
        _ => "unknown"
    };

    private static ActiveChatSessionInfo CreateActiveSession(ChatEngineSessionStartRequest parameters) =>
        new(
            parameters.RuntimeReference ?? throw new InvalidOperationException("Chat session runtime reference is not configured."),
            parameters.RuntimeDefaultModel,
            new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(
                parameters.RuntimeInputs,
                StringComparer.OrdinalIgnoreCase)),
            SnapshotOverrides(parameters.Overrides));

    private static ActiveChatSessionInfo SnapshotActiveSession(ActiveChatSessionInfo session) =>
        new(
            session.RuntimeReference,
            session.Model,
            new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(
                session.Inputs,
                StringComparer.OrdinalIgnoreCase)),
            SnapshotOverrides(session.Overrides));

    private static AgentSessionOverrides SnapshotOverrides(AgentSessionOverrides overrides) =>
        new()
        {
            McpServerBindings = overrides.McpServerBindings?
                .Select(static binding => binding.Clone())
                .ToList()
                .AsReadOnly(),
            WorkspacePath = overrides.WorkspacePath,
            SandboxProfileId = overrides.SandboxProfileId
        };

    private void EnsureNoIncompleteBackgroundTasks(AIAgent agent, AgentSession session, string operation)
    {
#pragma warning disable MAAI001
        var backgroundAgents = agent.GetService<BackgroundAgentsProvider>();
        if (backgroundAgents is not null && backgroundAgents.GetIncompleteTasks(session).Any())
            throw new InvalidOperationException($"The current Harness session cannot be {operation} while Background Agents are still running. Wait for them to finish and try again.");
#pragma warning restore MAAI001
    }

    private async Task DisposeSandboxAfterRestoreFailureAsync(SandboxSessionHandle sandbox)
    {
        try
        { await sandbox.DisposeAsync(); }
        catch (Exception ex) { logger.LogWarning(ex, "Could not dispose the replacement sandbox after restore preparation failed."); }
    }

    private static void ValidateSnapshotStructure(HarnessSessionSnapshot snapshot)
    {
        if (snapshot.Overrides is null ||
            snapshot.Session.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ||
            !AreValidMcpBindings(snapshot.Overrides.McpServerBindings))
        {
            throw new InvalidOperationException("The selected file is not a valid Harness session snapshot.");
        }
    }

    private static bool AreValidMcpBindings(IReadOnlyCollection<McpServerSessionBinding>? bindings) =>
        bindings is null || bindings.All(static binding =>
            binding is not null &&
            binding.SelectedTools is not null &&
            binding.Roots is not null &&
            binding.Parameters is not null &&
            binding.SelectedTools.All(static tool => tool is not null) &&
            binding.Roots.All(static root => root is not null));

    private static bool HaveEquivalentMcpBindings(
        IReadOnlyCollection<McpServerSessionBinding>? left,
        IReadOnlyCollection<McpServerSessionBinding>? right)
    {
        var unmatched = (right ?? []).ToList();
        foreach (var binding in left ?? [])
        {
            var index = unmatched.FindIndex(candidate => HaveEquivalentMcpBinding(binding, candidate));
            if (index < 0)
                return false;
            unmatched.RemoveAt(index);
        }

        return unmatched.Count == 0;
    }

    private static bool HaveEquivalentMcpBinding(McpServerSessionBinding left, McpServerSessionBinding right) =>
        left.BindingId == right.BindingId &&
        left.ServerId == right.ServerId &&
        string.Equals(left.ServerName?.Trim(), right.ServerName?.Trim(), StringComparison.OrdinalIgnoreCase) &&
        left.Enabled == right.Enabled &&
        left.SelectAllTools == right.SelectAllTools &&
        HaveEquivalentValues(left.SelectedTools, right.SelectedTools) &&
        HaveEquivalentValues(left.Roots, right.Roots) &&
        HaveEquivalentParameters(left.Parameters, right.Parameters);

    private static bool HaveEquivalentValues(IEnumerable<string> left, IEnumerable<string> right) =>
        left.Order(StringComparer.Ordinal).SequenceEqual(right.Order(StringComparer.Ordinal), StringComparer.Ordinal);

    private static bool HaveEquivalentParameters(
        IReadOnlyDictionary<string, string?> left,
        IReadOnlyDictionary<string, string?> right)
    {
        if (left.Count != right.Count)
            return false;

        var unmatched = right.ToList();
        foreach (var (key, value) in left)
        {
            var index = unmatched.FindIndex(pair =>
                string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(pair.Value, value, StringComparison.Ordinal));
            if (index < 0)
                return false;
            unmatched.RemoveAt(index);
        }

        return unmatched.Count == 0;
    }

    private static string ResolveRuntimeDisplayName(ChatEngineSessionStartRequest request) =>
        request.Agents.FirstOrDefault()?.Agent.AgentName ??
        request.RuntimeReference?.Kind.ToString() ??
        "Agent";

    private static AgentInputAttachment ToAgentInputAttachment(AppChatMessageFile file)
    {
        var content = IsTextAttachment(file)
            ? Encoding.UTF8.GetString(file.Data)
            : Convert.ToBase64String(file.Data);

        return new AgentInputAttachment(file.Name, file.ContentType, content)
        {
            Data = file.Data
        };
    }

    private static bool IsTextAttachment(AppChatMessageFile file) =>
        file.ContentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase) ||
        file.Name.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ||
        file.Name.EndsWith(".markdown", StringComparison.OrdinalIgnoreCase) ||
        file.Name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase);
}

#pragma warning restore MAAI001

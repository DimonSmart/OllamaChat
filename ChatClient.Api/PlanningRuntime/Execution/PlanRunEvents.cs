using ChatClient.Api.PlanningRuntime.Common;
using ChatClient.Api.PlanningRuntime.LowLevel;
using ChatClient.Api.PlanningRuntime.Outline;
using ChatClient.Api.PlanningRuntime.Planning;
using ChatClient.Api.PlanningRuntime.Runtime;
using ChatClient.Api.PlanningRuntime.Shared;
using ChatClient.Api.PlanningRuntime.Verification;
using System.Text.Json;

namespace ChatClient.Api.PlanningRuntime.Execution;

public interface IPlanRunObserver
{
    void OnEvent(PlanRunEvent planRunEvent);
}

public sealed class NullPlanRunObserver : IPlanRunObserver
{
    public static readonly NullPlanRunObserver Instance = new();

    private NullPlanRunObserver()
    {
    }

    public void OnEvent(PlanRunEvent planRunEvent)
    {
    }
}

public sealed class ActionPlanRunObserver(Action<PlanRunEvent> onEvent) : IPlanRunObserver
{
    public void OnEvent(PlanRunEvent planRunEvent)
    {
        onEvent(planRunEvent);
    }
}

public abstract record PlanRunEvent(DateTimeOffset OccurredAtUtc);

public sealed record DiagnosticPlanRunEvent(
    string Source,
    string Message) : PlanRunEvent(DateTimeOffset.UtcNow);

public sealed record PlanningAttemptStartedEvent(
    int AttemptNumber,
    string Phase,
    string UserQuery) : PlanRunEvent(DateTimeOffset.UtcNow);

public sealed record RequestAnalysisCompletedEvent(
    RequestBrief Brief) : PlanRunEvent(DateTimeOffset.UtcNow);

public sealed record OutlineStageCompletedEvent(
    OutlinePlan? Plan,
    string RawResponse,
    IReadOnlyList<PlanningIssue> Issues,
    bool IsValid) : PlanRunEvent(DateTimeOffset.UtcNow);

public sealed record LowLevelStageCompletedEvent(
    LowLevelPlan? Plan,
    string RawResponse,
    IReadOnlyList<PlanningIssue> Issues,
    bool IsValid) : PlanRunEvent(DateTimeOffset.UtcNow);

public sealed record RuntimeCompilationCompletedEvent(
    RuntimePlan? Plan,
    IReadOnlyList<PlanningIssue> Issues,
    bool IsSuccess) : PlanRunEvent(DateTimeOffset.UtcNow);

public sealed record RuntimeStepStartedEvent(
    string StepId,
    string Kind,
    JsonElement ResolvedInputs) : PlanRunEvent(DateTimeOffset.UtcNow);

public sealed record RuntimeStepCompletedEvent(
    string StepId,
    bool Ok,
    JsonElement? Output,
    ErrorInfo? Error) : PlanRunEvent(DateTimeOffset.UtcNow);

public sealed record RuntimeExecutionCompletedEvent(
    IReadOnlyList<PlanningIssue> Issues,
    bool IsSuccess) : PlanRunEvent(DateTimeOffset.UtcNow);

public sealed record PlanCreatedEvent(
    int AttemptNumber,
    string Phase,
    PlanDefinition Plan) : PlanRunEvent(DateTimeOffset.UtcNow);

public sealed record StepReusedEvent(
    string StepId) : PlanRunEvent(DateTimeOffset.UtcNow);

public sealed record StepStartedEvent(
    string StepId,
    string Kind,
    string Name,
    JsonElement ResolvedInputs,
    int? FanOutCount) : PlanRunEvent(DateTimeOffset.UtcNow);

public sealed record StepCallStartedEvent(
    string StepId,
    int CallIndex,
    JsonElement Input) : PlanRunEvent(DateTimeOffset.UtcNow);

public sealed record StepCallCompletedEvent(
    string StepId,
    int CallIndex,
    bool Ok,
    JsonElement? Output,
    ErrorInfo? Error) : PlanRunEvent(DateTimeOffset.UtcNow);

public sealed record AgentPromptPreparedEvent(
    string StepId,
    string AgentName,
    string SystemPrompt,
    string UserPrompt,
    string FullUserPrompt,
    JsonElement ResolvedInputs) : PlanRunEvent(DateTimeOffset.UtcNow);

public sealed record AgentResponseReceivedEvent(
    string StepId,
    string AgentName,
    string RawResponseText,
    bool Ok,
    JsonElement? Data,
    ErrorInfo? Error) : PlanRunEvent(DateTimeOffset.UtcNow);

public sealed record StepCompletedEvent(
    StepExecutionTrace Trace,
    JsonElement? Result) : PlanRunEvent(DateTimeOffset.UtcNow);

public sealed record GoalVerifiedEvent(
    GoalVerdict Verdict) : PlanRunEvent(DateTimeOffset.UtcNow);

public sealed record FinalAnswerVerifiedEvent(
    FinalAnswerVerificationResult VerificationResult) : PlanRunEvent(DateTimeOffset.UtcNow);

public sealed record FinalAnswerVerificationFailedEvent(
    string ErrorType,
    string Message) : PlanRunEvent(DateTimeOffset.UtcNow);

public sealed record ReplanStartedEvent(
    PlannerReplanRequest Request) : PlanRunEvent(DateTimeOffset.UtcNow);

public sealed record ReplanRoundCompletedEvent(
    int Round,
    bool Done,
    string Reason,
    JsonElement ActionBatch,
    JsonElement ActionResults) : PlanRunEvent(DateTimeOffset.UtcNow);

public sealed record ReplanAppliedEvent(
    PlanDefinition Plan) : PlanRunEvent(DateTimeOffset.UtcNow);

public sealed record RunCompletedEvent(
    ResultEnvelope<JsonElement?> Result,
    PlanDefinition? FinalPlan) : PlanRunEvent(DateTimeOffset.UtcNow);

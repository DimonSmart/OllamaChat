using ChatClient.Api.PlanningRuntime.Common;
using ChatClient.Api.PlanningRuntime.Execution;
using ChatClient.Api.PlanningRuntime.LowLevel;
using ChatClient.Api.PlanningRuntime.Outline;
using ChatClient.Api.PlanningRuntime.Planning;
using ChatClient.Api.PlanningRuntime.Runtime;
using ChatClient.Api.PlanningRuntime.Shared;
using System.Text.Json;

namespace ChatClient.Api.Client.Components.Planning;

public enum RuntimeWorkflowNodeKind
{
    RequestBrief,
    OutlinePlan,
    LowLevelPlan,
    RuntimePlan,
    Step,
    Result
}

public enum RuntimeWorkflowLinkKind
{
    Stage,
    Dependency,
    Result
}

public sealed record RuntimeWorkflowStepTrace
{
    public string StepId { get; init; } = string.Empty;

    public string Status { get; init; } = "todo";

    public JsonElement? ResolvedInputs { get; init; }

    public JsonElement? Output { get; init; }

    public ErrorInfo? Error { get; init; }
}

public sealed record RuntimeWorkflowNodeDescriptor
{
    public required string Id { get; init; }

    public required RuntimeWorkflowNodeKind Kind { get; init; }

    public required string Title { get; init; }

    public required string Subtitle { get; init; }

    public required string Status { get; init; }

    public required string Summary { get; init; }

    public required double X { get; init; }

    public required double Y { get; init; }

    public bool IsActive { get; init; }

    public RuntimeStep? Step { get; init; }

    public RuntimeWorkflowStepTrace? Trace { get; init; }

    public ResultEnvelope<JsonElement?>? FinalResult { get; init; }

    public RequestBrief? RequestBrief { get; init; }

    public OutlinePlan? OutlinePlan { get; init; }

    public LowLevelPlan? LowLevelPlan { get; init; }

    public RuntimePlan? CompiledPlan { get; init; }

    public IReadOnlyList<PlanningIssue> Issues { get; init; } = [];
}

public sealed record RuntimeWorkflowLinkDescriptor
{
    public required string Id { get; init; }

    public required string SourceId { get; init; }

    public required string TargetId { get; init; }

    public required RuntimeWorkflowLinkKind Kind { get; init; }

    public required string Label { get; init; }

    public string? Mode { get; init; }

    public string? From { get; init; }
}

public sealed record RuntimeWorkflowGraphDescriptor
{
    public IReadOnlyList<RuntimeWorkflowNodeDescriptor> Nodes { get; init; } = [];

    public IReadOnlyList<RuntimeWorkflowLinkDescriptor> Links { get; init; } = [];

    public double Width { get; init; }

    public double Height { get; init; }

    public double Left { get; init; }

    public double Top { get; init; }

    public double Right { get; init; }

    public double Bottom { get; init; }

    public string? DefaultSelectionId { get; init; }

    public bool HasContent => Nodes.Count > 0;

    internal PlanningGraphBounds Bounds => new(Left, Top, Right, Bottom);
}

public static class RuntimeWorkflowGraphProjection
{
    public const string RequestBriefNodeId = "__runtime_request_brief__";
    public const string OutlinePlanNodeId = "__runtime_outline_plan__";
    public const string LowLevelPlanNodeId = "__runtime_low_level_plan__";
    public const string RuntimePlanNodeId = "__runtime_plan__";
    public const string ResultNodeId = "__runtime_result__";

    public const double NodeWidth = 252d;
    public const double NodeHeight = 104d;

    private const double HorizontalGap = 132d;
    private const double VerticalGap = 56d;
    private const double Margin = 24d;
    private const double ResultGap = 168d;

    public static RuntimeWorkflowGraphDescriptor Build(
        RuntimePlan? plan,
        IReadOnlyList<PlanRunEvent> events,
        ResultEnvelope<JsonElement?>? finalResult,
        string? activeRuntimeStepId)
    {
        var artifactContext = RuntimeWorkflowArtifactContext.Create(events, plan, finalResult, activeRuntimeStepId);
        var artifactNodes = BuildArtifactNodes(artifactContext);
        var tracesByStepId = BuildStepTraces(events, activeRuntimeStepId);
        var stepNodes = BuildStepNodes(
            plan,
            tracesByStepId,
            activeRuntimeStepId,
            artifactNodes.Count > 0 ? NodeWidth + HorizontalGap : 0d);

        var nodes = new List<RuntimeWorkflowNodeDescriptor>(artifactNodes.Count + stepNodes.Count + 1);
        nodes.AddRange(artifactNodes);
        nodes.AddRange(stepNodes);

        if (ShouldShowResultNode(plan, finalResult))
        {
            nodes.Add(BuildResultNode(
                artifactNodes,
                stepNodes,
                plan,
                finalResult,
                artifactContext.RuntimeExecution?.Issues ?? []));
        }

        if (nodes.Count == 0)
        {
            return new RuntimeWorkflowGraphDescriptor
            {
                Width = NodeWidth + Margin * 2d,
                Height = NodeHeight + Margin * 2d,
                Left = Margin,
                Top = Margin,
                Right = Margin + NodeWidth,
                Bottom = Margin + NodeHeight
            };
        }

        var links = new List<RuntimeWorkflowLinkDescriptor>();
        links.AddRange(BuildStageLinks(artifactNodes, stepNodes, nodes, finalResult));
        links.AddRange(BuildDependencyLinks(plan));
        AddResultLink(links, plan, stepNodes, nodes, finalResult);

        var left = nodes.Min(static node => node.X);
        var top = nodes.Min(static node => node.Y);
        var right = nodes.Max(static node => node.X + NodeWidth);
        var bottom = nodes.Max(static node => node.Y + NodeHeight);

        return new RuntimeWorkflowGraphDescriptor
        {
            Nodes = nodes,
            Links = links,
            Width = right + Margin,
            Height = bottom + Margin,
            Left = left,
            Top = top,
            Right = right,
            Bottom = bottom,
            DefaultSelectionId = ResolveDefaultSelectionId(nodes, activeRuntimeStepId, finalResult)
        };
    }

    private static bool ShouldShowResultNode(RuntimePlan? plan, ResultEnvelope<JsonElement?>? finalResult) =>
        plan is not null || finalResult is not null;

    private static RuntimeWorkflowNodeDescriptor BuildResultNode(
        IReadOnlyList<RuntimeWorkflowNodeDescriptor> artifactNodes,
        IReadOnlyList<RuntimeWorkflowNodeDescriptor> stepNodes,
        RuntimePlan? plan,
        ResultEnvelope<JsonElement?>? finalResult,
        IReadOnlyList<PlanningIssue> executionIssues)
    {
        double x;
        double y;

        if (stepNodes.Count > 0)
        {
            var anchorNode = stepNodes.FirstOrDefault(node =>
                string.Equals(node.Id, plan?.ResultStepId, StringComparison.OrdinalIgnoreCase));
            var terminalNodes = GetTerminalStepIds(stepNodes, plan?.Steps ?? [])
                .Select(terminalId => stepNodes.First(node => string.Equals(node.Id, terminalId, StringComparison.Ordinal)))
                .ToList();
            var fallbackAnchorY = terminalNodes.Count == 0
                ? stepNodes.Average(static node => node.Y)
                : terminalNodes.Average(static node => node.Y);

            x = stepNodes.Max(static node => node.X) + NodeWidth + ResultGap;
            y = anchorNode?.Y ?? fallbackAnchorY;
        }
        else
        {
            x = Margin + NodeWidth + HorizontalGap;
            y = artifactNodes.LastOrDefault()?.Y ?? Margin;
        }

        return new RuntimeWorkflowNodeDescriptor
        {
            Id = ResultNodeId,
            Kind = RuntimeWorkflowNodeKind.Result,
            Title = "final result",
            Subtitle = BuildResultSubtitle(finalResult),
            Status = finalResult is null
                ? "todo"
                : finalResult.Ok
                    ? "done"
                    : "fail",
            Summary = BuildResultSummary(finalResult),
            X = x,
            Y = y,
            FinalResult = CloneEnvelope(finalResult),
            Issues = CloneIssues(executionIssues)
        };
    }

    private static IReadOnlyList<RuntimeWorkflowNodeDescriptor> BuildArtifactNodes(RuntimeWorkflowArtifactContext context)
    {
        var nodes = new List<RuntimeWorkflowNodeDescriptor>(capacity: 4);
        var index = 0;

        if (context.ShouldShowRequestBriefNode)
        {
            nodes.Add(new RuntimeWorkflowNodeDescriptor
            {
                Id = RequestBriefNodeId,
                Kind = RuntimeWorkflowNodeKind.RequestBrief,
                Title = "request brief",
                Subtitle = BuildRequestBriefSubtitle(context.RequestBrief),
                Status = ResolveRequestBriefStatus(context),
                Summary = BuildRequestBriefSummary(context.RequestBrief, context.RunFailed),
                X = Margin,
                Y = Margin + index++ * (NodeHeight + VerticalGap),
                RequestBrief = context.RequestBrief
            });
        }

        if (context.ShouldShowOutlineNode)
        {
            nodes.Add(new RuntimeWorkflowNodeDescriptor
            {
                Id = OutlinePlanNodeId,
                Kind = RuntimeWorkflowNodeKind.OutlinePlan,
                Title = "outline plan",
                Subtitle = BuildOutlineSubtitle(context.OutlineStage?.Plan),
                Status = ResolveOutlineStatus(context),
                Summary = BuildOutlineSummary(context.OutlineStage),
                X = Margin,
                Y = Margin + index++ * (NodeHeight + VerticalGap),
                OutlinePlan = context.OutlineStage?.Plan,
                Issues = CloneIssues(context.OutlineStage?.Issues)
            });
        }

        if (context.ShouldShowLowLevelNode)
        {
            nodes.Add(new RuntimeWorkflowNodeDescriptor
            {
                Id = LowLevelPlanNodeId,
                Kind = RuntimeWorkflowNodeKind.LowLevelPlan,
                Title = "low-level plan",
                Subtitle = BuildLowLevelSubtitle(context.LowLevelStage?.Plan),
                Status = ResolveLowLevelStatus(context),
                Summary = BuildLowLevelSummary(context.LowLevelStage),
                X = Margin,
                Y = Margin + index++ * (NodeHeight + VerticalGap),
                LowLevelPlan = context.LowLevelStage?.Plan,
                Issues = CloneIssues(context.LowLevelStage?.Issues)
            });
        }

        if (context.ShouldShowRuntimePlanNode)
        {
            nodes.Add(new RuntimeWorkflowNodeDescriptor
            {
                Id = RuntimePlanNodeId,
                Kind = RuntimeWorkflowNodeKind.RuntimePlan,
                Title = "runtime plan",
                Subtitle = BuildRuntimePlanSubtitle(context.RuntimeCompilation?.Plan ?? context.RuntimePlan),
                Status = ResolveRuntimePlanStatus(context),
                Summary = BuildRuntimePlanSummary(context.RuntimeCompilation, context.RuntimePlan),
                X = Margin,
                Y = Margin + index * (NodeHeight + VerticalGap),
                CompiledPlan = context.RuntimeCompilation?.Plan ?? context.RuntimePlan,
                Issues = CloneIssues(context.RuntimeCompilation?.Issues)
            });
        }

        return nodes;
    }

    private static IReadOnlyList<RuntimeWorkflowNodeDescriptor> BuildStepNodes(
        RuntimePlan? plan,
        IReadOnlyDictionary<string, RuntimeWorkflowStepTrace> tracesByStepId,
        string? activeRuntimeStepId,
        double leftOffset)
    {
        if (plan is null || plan.Steps.Count == 0)
        {
            return [];
        }

        var depthByStepId = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var nodes = new List<RuntimeWorkflowNodeDescriptor>(plan.Steps.Count);

        for (var index = 0; index < plan.Steps.Count; index++)
        {
            var step = plan.Steps[index];
            var depth = GetDependencies(step)
                .Where(depthByStepId.ContainsKey)
                .Select(dependency => depthByStepId[dependency] + 1)
                .DefaultIfEmpty(0)
                .Max();

            depthByStepId[step.Id] = depth;
            tracesByStepId.TryGetValue(step.Id, out var trace);
            nodes.Add(new RuntimeWorkflowNodeDescriptor
            {
                Id = step.Id,
                Kind = RuntimeWorkflowNodeKind.Step,
                Title = step.Id,
                Subtitle = BuildStepSubtitle(step),
                Status = trace?.Status
                    ?? (string.Equals(activeRuntimeStepId, step.Id, StringComparison.OrdinalIgnoreCase) ? "running" : "todo"),
                Summary = BuildStepSummary(step),
                X = Margin + leftOffset + depth * (NodeWidth + HorizontalGap),
                Y = Margin + index * (NodeHeight + VerticalGap),
                IsActive = string.Equals(activeRuntimeStepId, step.Id, StringComparison.OrdinalIgnoreCase),
                Step = step,
                Trace = trace
            });
        }

        return nodes;
    }

    private static List<RuntimeWorkflowLinkDescriptor> BuildStageLinks(
        IReadOnlyList<RuntimeWorkflowNodeDescriptor> artifactNodes,
        IReadOnlyList<RuntimeWorkflowNodeDescriptor> stepNodes,
        IReadOnlyList<RuntimeWorkflowNodeDescriptor> allNodes,
        ResultEnvelope<JsonElement?>? finalResult)
    {
        var links = new List<RuntimeWorkflowLinkDescriptor>();
        var nodeIds = allNodes.Select(static node => node.Id).ToHashSet(StringComparer.Ordinal);

        AddStageLinkIfPresent(links, nodeIds, RequestBriefNodeId, OutlinePlanNodeId, "outline");
        AddStageLinkIfPresent(links, nodeIds, OutlinePlanNodeId, LowLevelPlanNodeId, "lower");
        AddStageLinkIfPresent(links, nodeIds, LowLevelPlanNodeId, RuntimePlanNodeId, "compile");

        var runtimeEntrySourceId = nodeIds.Contains(RuntimePlanNodeId)
            ? RuntimePlanNodeId
            : nodeIds.Contains(LowLevelPlanNodeId)
                ? LowLevelPlanNodeId
                : nodeIds.Contains(OutlinePlanNodeId)
                    ? OutlinePlanNodeId
                    : nodeIds.Contains(RequestBriefNodeId)
                        ? RequestBriefNodeId
                        : null;

        if (!string.IsNullOrWhiteSpace(runtimeEntrySourceId) && stepNodes.Count > 0)
        {
            var rootStepIds = GetRootStepIds(stepNodes);
            foreach (var rootStepId in rootStepIds)
            {
                links.Add(new RuntimeWorkflowLinkDescriptor
                {
                    Id = CreateLinkId(runtimeEntrySourceId, rootStepId, RuntimeWorkflowLinkKind.Stage),
                    SourceId = runtimeEntrySourceId,
                    TargetId = rootStepId,
                    Kind = RuntimeWorkflowLinkKind.Stage,
                    Label = "entry"
                });
            }
        }

        if (finalResult is not null && stepNodes.Count == 0)
        {
            var finalSourceId = artifactNodes.LastOrDefault()?.Id;
            if (!string.IsNullOrWhiteSpace(finalSourceId) && nodeIds.Contains(ResultNodeId))
            {
                links.Add(new RuntimeWorkflowLinkDescriptor
                {
                    Id = CreateLinkId(finalSourceId, ResultNodeId, RuntimeWorkflowLinkKind.Result),
                    SourceId = finalSourceId,
                    TargetId = ResultNodeId,
                    Kind = RuntimeWorkflowLinkKind.Result,
                    Label = "result"
                });
            }
        }

        return links;
    }

    private static void AddStageLinkIfPresent(
        ICollection<RuntimeWorkflowLinkDescriptor> links,
        IReadOnlySet<string> nodeIds,
        string sourceId,
        string targetId,
        string label)
    {
        if (!nodeIds.Contains(sourceId) || !nodeIds.Contains(targetId))
        {
            return;
        }

        links.Add(new RuntimeWorkflowLinkDescriptor
        {
            Id = CreateLinkId(sourceId, targetId, RuntimeWorkflowLinkKind.Stage),
            SourceId = sourceId,
            TargetId = targetId,
            Kind = RuntimeWorkflowLinkKind.Stage,
            Label = label
        });
    }

    private static List<RuntimeWorkflowLinkDescriptor> BuildDependencyLinks(RuntimePlan? plan)
    {
        var links = new List<RuntimeWorkflowLinkDescriptor>();
        if (plan is null)
        {
            return links;
        }

        foreach (var step in plan.Steps)
        {
            foreach (var input in step.In)
            {
                if (!string.Equals(input.Value.Kind, RuntimeInputValueKinds.Binding, StringComparison.OrdinalIgnoreCase)
                    || !RuntimeBindingResolver.TryParseBindingPath(input.Value.From ?? string.Empty, out var sourceStepId, out _))
                {
                    continue;
                }

                links.Add(new RuntimeWorkflowLinkDescriptor
                {
                    Id = $"{sourceStepId}->{step.Id}:{input.Key}",
                    SourceId = sourceStepId,
                    TargetId = step.Id,
                    Kind = RuntimeWorkflowLinkKind.Dependency,
                    Label = input.Key,
                    Mode = input.Value.Mode,
                    From = input.Value.From
                });
            }
        }

        return links;
    }

    private static void AddResultLink(
        ICollection<RuntimeWorkflowLinkDescriptor> links,
        RuntimePlan? plan,
        IReadOnlyList<RuntimeWorkflowNodeDescriptor> stepNodes,
        IReadOnlyList<RuntimeWorkflowNodeDescriptor> nodes,
        ResultEnvelope<JsonElement?>? finalResult)
    {
        if (plan is not null
            && stepNodes.Count > 0
            && nodes.Any(node => string.Equals(node.Id, ResultNodeId, StringComparison.Ordinal)))
        {
            links.Add(new RuntimeWorkflowLinkDescriptor
            {
                Id = CreateLinkId(plan.ResultStepId, ResultNodeId, RuntimeWorkflowLinkKind.Result),
                SourceId = plan.ResultStepId,
                TargetId = ResultNodeId,
                Kind = RuntimeWorkflowLinkKind.Result,
                Label = string.IsNullOrWhiteSpace(plan.ResultPort) ? "result" : plan.ResultPort,
                Mode = "value",
                From = $"${plan.ResultStepId}.{plan.ResultPort}"
            });
            return;
        }

        if (finalResult is not null && nodes.Count == 1 && string.Equals(nodes[0].Id, ResultNodeId, StringComparison.Ordinal))
        {
            return;
        }
    }

    private static string? ResolveDefaultSelectionId(
        IReadOnlyList<RuntimeWorkflowNodeDescriptor> nodes,
        string? activeRuntimeStepId,
        ResultEnvelope<JsonElement?>? finalResult)
    {
        if (!string.IsNullOrWhiteSpace(activeRuntimeStepId)
            && nodes.Any(node => string.Equals(node.Id, activeRuntimeStepId, StringComparison.OrdinalIgnoreCase)))
        {
            return activeRuntimeStepId;
        }

        if (finalResult is not null && nodes.Any(node => node.Kind == RuntimeWorkflowNodeKind.Result))
        {
            return ResultNodeId;
        }

        return nodes.FirstOrDefault(node => node.Kind == RuntimeWorkflowNodeKind.Step)?.Id
            ?? nodes.FirstOrDefault(node => node.Kind == RuntimeWorkflowNodeKind.RuntimePlan && IsMaterializedArtifact(node))?.Id
            ?? nodes.FirstOrDefault(node => node.Kind == RuntimeWorkflowNodeKind.LowLevelPlan && IsMaterializedArtifact(node))?.Id
            ?? nodes.FirstOrDefault(node => node.Kind == RuntimeWorkflowNodeKind.OutlinePlan && IsMaterializedArtifact(node))?.Id
            ?? nodes.FirstOrDefault(node => node.Kind == RuntimeWorkflowNodeKind.RequestBrief && IsMaterializedArtifact(node))?.Id
            ?? nodes.FirstOrDefault()?.Id;
    }

    private static bool IsMaterializedArtifact(RuntimeWorkflowNodeDescriptor node) => node.Kind switch
    {
        RuntimeWorkflowNodeKind.RequestBrief => node.RequestBrief is not null,
        RuntimeWorkflowNodeKind.OutlinePlan => node.OutlinePlan is not null || node.Issues.Count > 0,
        RuntimeWorkflowNodeKind.LowLevelPlan => node.LowLevelPlan is not null || node.Issues.Count > 0,
        RuntimeWorkflowNodeKind.RuntimePlan => node.CompiledPlan is not null || node.Issues.Count > 0,
        _ => false
    };

    private static IReadOnlyCollection<string> GetDependencies(RuntimeStep step)
    {
        var dependencies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var input in step.In.Values)
        {
            if (!string.Equals(input.Kind, RuntimeInputValueKinds.Binding, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (RuntimeBindingResolver.TryParseBindingPath(input.From ?? string.Empty, out var sourceStepId, out _))
            {
                dependencies.Add(sourceStepId);
            }
        }

        return dependencies;
    }

    private static IReadOnlyCollection<string> GetRootStepIds(IReadOnlyList<RuntimeWorkflowNodeDescriptor> stepNodes)
    {
        var dependentStepIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in stepNodes)
        {
            if (node.Step is null)
            {
                continue;
            }

            foreach (var _ in GetDependencies(node.Step))
            {
                dependentStepIds.Add(node.Id);
            }
        }

        return stepNodes
            .Where(node => !dependentStepIds.Contains(node.Id))
            .Select(static node => node.Id)
            .ToList();
    }

    private static IReadOnlyCollection<string> GetTerminalStepIds(
        IReadOnlyList<RuntimeWorkflowNodeDescriptor> stepNodes,
        IReadOnlyList<RuntimeStep> steps)
    {
        var stepIdsWithDependents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var step in steps)
        {
            foreach (var dependency in GetDependencies(step))
            {
                stepIdsWithDependents.Add(dependency);
            }
        }

        return stepNodes
            .Where(node => !stepIdsWithDependents.Contains(node.Id))
            .Select(static node => node.Id)
            .ToList();
    }

    private static Dictionary<string, RuntimeWorkflowStepTrace> BuildStepTraces(
        IReadOnlyList<PlanRunEvent> events,
        string? activeRuntimeStepId)
    {
        var startedByStepId = events
            .OfType<RuntimeStepStartedEvent>()
            .GroupBy(static evt => evt.StepId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.Last(), StringComparer.OrdinalIgnoreCase);
        var completedByStepId = events
            .OfType<RuntimeStepCompletedEvent>()
            .GroupBy(static evt => evt.StepId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.Last(), StringComparer.OrdinalIgnoreCase);
        var stepIds = startedByStepId.Keys
            .Concat(completedByStepId.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase);

        var traces = new Dictionary<string, RuntimeWorkflowStepTrace>(StringComparer.OrdinalIgnoreCase);
        foreach (var stepId in stepIds)
        {
            startedByStepId.TryGetValue(stepId, out var started);
            completedByStepId.TryGetValue(stepId, out var completed);
            traces[stepId] = new RuntimeWorkflowStepTrace
            {
                StepId = stepId,
                Status = ResolveTraceStatus(stepId, started, completed, activeRuntimeStepId),
                ResolvedInputs = started?.ResolvedInputs.Clone(),
                Output = completed?.Output?.Clone(),
                Error = completed?.Error is null
                    ? null
                    : new ErrorInfo(
                        completed.Error.Code,
                        completed.Error.Message,
                        completed.Error.Details?.Clone())
            };
        }

        return traces;
    }

    private static string ResolveTraceStatus(
        string stepId,
        RuntimeStepStartedEvent? started,
        RuntimeStepCompletedEvent? completed,
        string? activeRuntimeStepId)
    {
        if (!string.IsNullOrWhiteSpace(activeRuntimeStepId)
            && string.Equals(stepId, activeRuntimeStepId, StringComparison.OrdinalIgnoreCase))
        {
            return "running";
        }

        if (completed is not null)
        {
            return completed.Ok ? "done" : "fail";
        }

        if (started is not null)
        {
            return "running";
        }

        return "todo";
    }

    private static string BuildRequestBriefSubtitle(RequestBrief? brief) =>
        brief is null
            ? "clarifying the user request"
            : $"deliverables: {brief.Deliverables.Count} | outline: {brief.SuggestedPlanOutline.Count}";

    private static string BuildRequestBriefSummary(RequestBrief? brief, bool runFailed)
    {
        if (!string.IsNullOrWhiteSpace(brief?.RewrittenRequest))
        {
            return Shorten(brief.RewrittenRequest, 140);
        }

        return runFailed ? "Request analysis did not complete." : "Waiting for analyzed request.";
    }

    private static string BuildOutlineSubtitle(OutlinePlan? outlinePlan)
    {
        if (outlinePlan is null)
        {
            return "building the stage graph";
        }

        return outlinePlan.IsBlocked
            ? "blocked"
            : $"nodes: {outlinePlan.Nodes.Count} | result: {outlinePlan.ResultNodeId ?? "<none>"}";
    }

    private static string BuildOutlineSummary(OutlineStageCompletedEvent? outlineStage)
    {
        if (outlineStage?.Plan is { } plan)
        {
            return !string.IsNullOrWhiteSpace(plan.BlockedReason)
                ? plan.BlockedReason
                : Shorten(plan.Goal, 140);
        }

        if (outlineStage is not null && outlineStage.Issues.Count > 0)
        {
            return Shorten(outlineStage.Issues[0].Message, 140);
        }

        return "Waiting for outline planner output.";
    }

    private static string BuildLowLevelSubtitle(LowLevelPlan? lowLevelPlan)
    {
        if (lowLevelPlan is null)
        {
            return "lowering into concrete steps";
        }

        return lowLevelPlan.IsBlocked
            ? "blocked"
            : $"steps: {lowLevelPlan.Steps.Count} | result: {lowLevelPlan.ResultStepId ?? "<none>"}";
    }

    private static string BuildLowLevelSummary(LowLevelStageCompletedEvent? lowLevelStage)
    {
        if (lowLevelStage?.Plan is { } plan)
        {
            return !string.IsNullOrWhiteSpace(plan.BlockedReason)
                ? plan.BlockedReason
                : Shorten(plan.Goal, 140);
        }

        if (lowLevelStage is not null && lowLevelStage.Issues.Count > 0)
        {
            return Shorten(lowLevelStage.Issues[0].Message, 140);
        }

        return "Waiting for low-level planner output.";
    }

    private static string BuildRuntimePlanSubtitle(RuntimePlan? runtimePlan)
    {
        if (runtimePlan is null)
        {
            return "compiling runtime workflow";
        }

        return $"steps: {runtimePlan.Steps.Count} | result: {runtimePlan.ResultStepId}.{runtimePlan.ResultPort}";
    }

    private static string BuildRuntimePlanSummary(
        RuntimeCompilationCompletedEvent? compilation,
        RuntimePlan? runtimePlan)
    {
        if (runtimePlan is not null)
        {
            return Shorten(runtimePlan.Goal, 140);
        }

        if (compilation is not null && compilation.Issues.Count > 0)
        {
            return Shorten(compilation.Issues[0].Message, 140);
        }

        return "Waiting for runtime compiler output.";
    }

    private static string BuildStepSubtitle(RuntimeStep step)
    {
        var kind = string.IsNullOrWhiteSpace(step.Kind) ? "step" : step.Kind;
        if (string.IsNullOrWhiteSpace(step.CapabilityId))
        {
            return kind;
        }

        var capability = step.CapabilityId;
        var separatorIndex = capability.LastIndexOf(':');
        var shortCapability = separatorIndex >= 0 && separatorIndex < capability.Length - 1
            ? capability[(separatorIndex + 1)..]
            : capability;
        return $"{kind} | {shortCapability}";
    }

    private static string BuildStepSummary(RuntimeStep step) =>
        Shorten(step.Purpose, 140);

    private static string BuildResultSubtitle(ResultEnvelope<JsonElement?>? finalResult)
    {
        if (finalResult is null)
        {
            return "awaiting execution";
        }

        return finalResult.Ok
            ? "ok: true"
            : $"error: {finalResult.Error?.Code ?? "runtime_execution_failed"}";
    }

    private static string BuildResultSummary(ResultEnvelope<JsonElement?>? finalResult)
    {
        if (finalResult is null)
        {
            return "Final result is not available yet.";
        }

        if (!finalResult.Ok)
        {
            return Shorten(finalResult.Error?.Message ?? finalResult.Error?.Code ?? "Planning failed.", 140);
        }

        if (TryExtractSummary(finalResult.Data, out var summary))
        {
            return Shorten(summary, 140);
        }

        return "Final result is available.";
    }

    private static bool TryExtractSummary(JsonElement? data, out string summary)
    {
        summary = string.Empty;
        if (data is not { } value || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return false;
        }

        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                summary = value.GetString() ?? string.Empty;
                return !string.IsNullOrWhiteSpace(summary);

            case JsonValueKind.Object:
                foreach (var fieldName in new[] { "summary", "answer", "result", "message", "text", "content" })
                {
                    if (!value.TryGetProperty(fieldName, out var property))
                    {
                        continue;
                    }

                    if (property.ValueKind == JsonValueKind.String)
                    {
                        summary = property.GetString() ?? string.Empty;
                        if (!string.IsNullOrWhiteSpace(summary))
                        {
                            return true;
                        }
                    }
                }

                return false;

            default:
                return false;
        }
    }

    private static string ResolveRequestBriefStatus(RuntimeWorkflowArtifactContext context)
    {
        if (context.RequestBrief is not null)
        {
            return "done";
        }

        return context.RunFailed ? "fail" : "running";
    }

    private static string ResolveOutlineStatus(RuntimeWorkflowArtifactContext context)
    {
        if (context.OutlineStage is not null)
        {
            return context.OutlineStage.IsValid
                && context.OutlineStage.Plan is not null
                && !context.OutlineStage.Plan.IsBlocked
                ? "done"
                : "fail";
        }

        return context.RunFailed ? "fail" : "running";
    }

    private static string ResolveLowLevelStatus(RuntimeWorkflowArtifactContext context)
    {
        if (context.LowLevelStage is not null)
        {
            return context.LowLevelStage.IsValid
                && context.LowLevelStage.Plan is not null
                && !context.LowLevelStage.Plan.IsBlocked
                ? "done"
                : "fail";
        }

        return context.RunFailed ? "fail" : "running";
    }

    private static string ResolveRuntimePlanStatus(RuntimeWorkflowArtifactContext context)
    {
        if (context.RuntimeCompilation is not null)
        {
            return context.RuntimeCompilation.IsSuccess && context.RuntimeCompilation.Plan is not null
                ? "done"
                : "fail";
        }

        return context.RunFailed ? "fail" : "running";
    }

    private static IReadOnlyList<PlanningIssue> CloneIssues(IReadOnlyList<PlanningIssue>? issues) =>
        issues is null
            ? []
            : issues.Select(static issue => new PlanningIssue
            {
                Layer = issue.Layer,
                Code = issue.Code,
                Message = issue.Message,
                Details = issue.Details is JsonElement details ? details.Clone() : null,
                IsBlocking = issue.IsBlocking
            }).ToList();

    private static string Shorten(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.ReplaceLineEndings(" ").Trim();
        return normalized.Length <= maxLength
            ? normalized
            : $"{normalized[..maxLength]}...";
    }

    private static string CreateLinkId(string sourceId, string targetId, RuntimeWorkflowLinkKind kind) =>
        $"runtime-link:{kind.ToString().ToLowerInvariant()}:{sourceId}->{targetId}";

    private static ResultEnvelope<JsonElement?>? CloneEnvelope(ResultEnvelope<JsonElement?>? result) =>
        result is null
            ? null
            : result.Ok
                ? ResultEnvelope<JsonElement?>.Success(result.Data?.Clone())
                : ResultEnvelope<JsonElement?>.Failure(
                    result.Error?.Code ?? "planning_failed",
                    result.Error?.Message ?? "Planning failed.",
                    result.Error?.Details?.Clone());

    private sealed record RuntimeWorkflowArtifactContext(
        bool RunFailed,
        RequestBrief? RequestBrief,
        OutlineStageCompletedEvent? OutlineStage,
        LowLevelStageCompletedEvent? LowLevelStage,
        RuntimeCompilationCompletedEvent? RuntimeCompilation,
        RuntimePlan? RuntimePlan,
        RuntimeExecutionCompletedEvent? RuntimeExecution,
        bool ShouldShowRequestBriefNode,
        bool ShouldShowOutlineNode,
        bool ShouldShowLowLevelNode,
        bool ShouldShowRuntimePlanNode)
    {
        public static RuntimeWorkflowArtifactContext Create(
            IReadOnlyList<PlanRunEvent> events,
            RuntimePlan? runtimePlan,
            ResultEnvelope<JsonElement?>? finalResult,
            string? activeRuntimeStepId)
        {
            var planningStarted = events.OfType<PlanningAttemptStartedEvent>().Any();
            var requestBrief = events.OfType<RequestAnalysisCompletedEvent>().LastOrDefault()?.Brief;
            var outlineStage = events.OfType<OutlineStageCompletedEvent>().LastOrDefault();
            var lowLevelStage = events.OfType<LowLevelStageCompletedEvent>().LastOrDefault();
            var runtimeCompilation = events.OfType<RuntimeCompilationCompletedEvent>().LastOrDefault();
            var runtimeExecution = events.OfType<RuntimeExecutionCompletedEvent>().LastOrDefault();
            var hasRuntimeProgress = !string.IsNullOrWhiteSpace(activeRuntimeStepId)
                || events.OfType<RuntimeStepStartedEvent>().Any()
                || events.OfType<RuntimeStepCompletedEvent>().Any();
            var hasFinalResult = finalResult is not null || events.OfType<RunCompletedEvent>().Any();
            var runFailed = finalResult is { Ok: false };

            return new RuntimeWorkflowArtifactContext(
                RunFailed: runFailed,
                RequestBrief: requestBrief,
                OutlineStage: outlineStage,
                LowLevelStage: lowLevelStage,
                RuntimeCompilation: runtimeCompilation,
                RuntimePlan: runtimeCompilation?.Plan ?? runtimePlan,
                RuntimeExecution: runtimeExecution,
                ShouldShowRequestBriefNode: planningStarted
                    || requestBrief is not null
                    || outlineStage is not null
                    || lowLevelStage is not null
                    || runtimeCompilation is not null
                    || hasRuntimeProgress
                    || hasFinalResult,
                ShouldShowOutlineNode: requestBrief is not null
                    || outlineStage is not null
                    || lowLevelStage is not null
                    || runtimeCompilation is not null
                    || hasRuntimeProgress
                    || hasFinalResult,
                ShouldShowLowLevelNode: outlineStage is not null
                    || lowLevelStage is not null
                    || runtimeCompilation is not null
                    || hasRuntimeProgress
                    || hasFinalResult,
                ShouldShowRuntimePlanNode: lowLevelStage is not null
                    || runtimeCompilation is not null
                    || runtimePlan is not null
                    || hasRuntimeProgress
                    || hasFinalResult);
        }
    }
}

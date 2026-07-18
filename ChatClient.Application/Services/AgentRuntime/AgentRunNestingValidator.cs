using System.Text;

namespace ChatClient.Application.Services.AgentRuntime;

public sealed class AgentRunNestingValidator(
    AgentRuntimeOptions options) : IAgentRunNestingValidator
{
    public AgentRunNestingValidation Validate(
        AgentDefinitionDescriptor target,
        AgentRunContext context)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(context);

        var stack = context.DefinitionStack;
        if (stack.Count == 0)
        {
            return Invalid(
                "invalid_run_context",
                "Run definition stack must contain the target definition.");
        }

        if (stack.Any(static frame => string.IsNullOrWhiteSpace(frame.Definition.Id)))
        {
            return Invalid(
                "invalid_run_context",
                "Run definition stack contains an empty definition id.");
        }

        if (!SameReference(stack[^1].Definition, target.Reference))
        {
            return Invalid(
                "invalid_run_context",
                "Run definition stack does not end with the target definition.");
        }

        if (target.RuntimeKind != AgentRuntimeKind.WorkflowAgent)
        {
            return Valid();
        }

        var workflowDepth = stack.Count(static frame =>
            frame.Definition.Kind == AgentDefinitionKind.SavedWorkflow);
        if (workflowDepth > options.MaximumWorkflowNestingDepth)
        {
            return Invalid(
                "workflow_nesting_limit_exceeded",
                $"Workflow nesting limit exceeded. Maximum depth is {options.MaximumWorkflowNestingDepth}.",
                CreateMetadata(context, target, stack));
        }

        var priorFrames = stack.Count > 0 &&
                          SameReference(stack[^1].Definition, target.Reference)
            ? stack.Take(stack.Count - 1)
            : stack;
        if (!priorFrames.Any(frame => SameReference(frame.Definition, target.Reference)))
        {
            return Valid();
        }

        return Invalid(
            "workflow_cycle_detected",
            BuildCycleMessage(stack, target),
            CreateMetadata(context, target, stack));
    }

    private static string BuildCycleMessage(
        IReadOnlyList<AgentRunFrame> stack,
        AgentDefinitionDescriptor target)
    {
        var frames = stack.Count > 0 ? stack : [new AgentRunFrame
        {
            Definition = target.Reference,
            DisplayName = target.Name
        }];
        var builder = new StringBuilder();
        builder.AppendLine("Workflow dependency cycle detected:");

        foreach (var frame in frames)
        {
            builder.Append(frame.DisplayName);
            if (!string.IsNullOrWhiteSpace(frame.ParticipantId))
            {
                builder.Append(" via participant \"");
                builder.Append(frame.ParticipantDisplayName ?? frame.ParticipantId);
                builder.Append('"');
            }

            builder.AppendLine();
        }

        return builder.ToString().TrimEnd();
    }

    private static IReadOnlyDictionary<string, string> CreateMetadata(
        AgentRunContext context,
        AgentDefinitionDescriptor target,
        IReadOnlyList<AgentRunFrame> stack)
    {
        var metadata = new Dictionary<string, string>
        {
            ["run.id"] = context.RunId,
            ["definition.kind"] = target.Reference.Kind.ToString(),
            ["definition.id"] = target.Reference.Id,
            ["definition.name"] = target.Name,
            ["definition.stack"] = string.Join(
                " > ",
                stack.Select(static frame =>
                    $"{frame.Definition.Kind}:{frame.Definition.Id}:{frame.DisplayName}"))
        };

        if (!string.IsNullOrWhiteSpace(context.ParentRunId))
        {
            metadata["parentRun.id"] = context.ParentRunId;
        }

        var last = stack.LastOrDefault();
        if (last is not null)
        {
            if (!string.IsNullOrWhiteSpace(last.ParticipantId))
            {
                metadata["participant.id"] = last.ParticipantId;
            }

            if (!string.IsNullOrWhiteSpace(last.ParticipantDisplayName))
            {
                metadata["participant.name"] = last.ParticipantDisplayName;
            }
        }

        return metadata;
    }

    private static AgentRunNestingValidation Valid() =>
        new() { IsValid = true };

    private static AgentRunNestingValidation Invalid(
        string code,
        string message,
        IReadOnlyDictionary<string, string>? metadata = null) =>
        new()
        {
            IsValid = false,
            Error = new AgentRunError(code, message, false)
            {
                Metadata = metadata ?? new Dictionary<string, string>()
            }
        };

    private static bool SameReference(
        AgentDefinitionReference left,
        AgentDefinitionReference right) =>
        left.Kind == right.Kind &&
        string.Equals(
            NormalizeReferenceId(left.Id),
            NormalizeReferenceId(right.Id),
            StringComparison.OrdinalIgnoreCase);

    private static string NormalizeReferenceId(string id) =>
        Guid.TryParse(id, out var parsed)
            ? parsed.ToString("D")
            : id;
}

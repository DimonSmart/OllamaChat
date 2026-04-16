using ChatClient.Api.PlanningRuntime.Outline;
using ChatClient.Api.PlanningRuntime.Runtime;
using ChatClient.Api.PlanningRuntime.Shared;
using ChatClient.Api.Services;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChatClient.Api.PlanningRuntime.LowLevel;

public sealed class LowLevelValidationResult
{
    public bool IsValid => Issues.Count == 0;

    public List<PlanningIssue> Issues { get; } = [];
}

public static class LowLevelValidator
{
    public static LowLevelValidationResult Validate(
        LowLevelPlan plan,
        OutlinePlan outlinePlan,
        IReadOnlyCollection<AppToolDescriptor> tools)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(outlinePlan);
        ArgumentNullException.ThrowIfNull(tools);

        var result = new LowLevelValidationResult();
        if (string.IsNullOrWhiteSpace(plan.Goal))
            result.Issues.Add(CreateIssue("goal_missing", "LowLevelPlan.goal is required."));

        if (plan.Steps.Count == 0)
        {
            result.Issues.Add(CreateIssue("steps_empty", "LowLevelPlan.steps must not be empty."));
            return result;
        }

        var toolIds = tools.Select(static tool => tool.QualifiedName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var toolsById = tools.ToDictionary(tool => tool.QualifiedName, StringComparer.OrdinalIgnoreCase);
        var outlineById = outlinePlan.Nodes.ToDictionary(node => node.Id, StringComparer.OrdinalIgnoreCase);
        var outlineNodeIds = outlinePlan.Nodes.Select(static node => node.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var stepIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var index = 0; index < plan.Steps.Count; index++)
        {
            var step = plan.Steps[index];
            if (string.IsNullOrWhiteSpace(step.Id) || !stepIds.Add(step.Id))
                result.Issues.Add(CreateIssue("step_id_invalid", $"Low-level step at index {index} must have a unique id."));

            if (!outlineNodeIds.Contains(step.OutlineNodeId))
                result.Issues.Add(CreateIssue("outline_node_missing", $"Low-level step '{step.Id}' references unknown outline node '{step.OutlineNodeId}'."));

            if (!LowLevelStepKinds.All.Contains(step.Kind))
                result.Issues.Add(CreateIssue("step_kind_invalid", $"Low-level step '{step.Id}' has unsupported kind '{step.Kind}'."));

            if (!LowLevelFanoutModes.All.Contains(step.Fanout))
                result.Issues.Add(CreateIssue("fanout_invalid", $"Low-level step '{step.Id}' has unsupported fanout '{step.Fanout}'."));

            if (string.Equals(step.Kind, LowLevelStepKinds.Tool, StringComparison.OrdinalIgnoreCase)
                && string.IsNullOrWhiteSpace(step.CapabilityId))
            {
                result.Issues.Add(CreateIssue("capability_missing", $"Step '{step.Id}' requires capabilityId."));
            }

            if (string.Equals(step.Kind, LowLevelStepKinds.Tool, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(step.CapabilityId)
                && !toolIds.Contains(step.CapabilityId))
            {
                result.Issues.Add(CreateIssue("capability_unknown", $"Step '{step.Id}' references unknown capability '{step.CapabilityId}'."));
            }

            if (string.Equals(step.Kind, LowLevelStepKinds.Llm, StringComparison.OrdinalIgnoreCase)
                && string.IsNullOrWhiteSpace(step.Out?.Format))
            {
                result.Issues.Add(CreateIssue("out_format_missing", $"Step '{step.Id}' must declare out.format."));
            }

            if (outlineById.TryGetValue(step.OutlineNodeId, out var outlineNode))
                ValidateExecutionContract(step, outlineNode, toolsById, plan.ResultStepId, result);

            foreach (var input in step.Inputs)
                ValidateInput(plan, step, input, index, result);

            if (step.Outputs.Count == 0)
            {
                result.Issues.Add(CreateIssue("outputs_missing", $"Step '{step.Id}' must declare at least one output port."));
            }
            else
            {
                var portIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var output in step.Outputs)
                {
                    if (string.IsNullOrWhiteSpace(output.Name) || !portIds.Add(output.Name))
                        result.Issues.Add(CreateIssue("output_port_invalid", $"Step '{step.Id}' has an invalid or duplicate output port."));
                }
            }
        }

        if (string.IsNullOrWhiteSpace(plan.ResultStepId))
            result.Issues.Add(CreateIssue("result_step_missing", "LowLevelPlan.resultStepId is required."));

        var resultSteps = plan.Steps.Where(static step => step.IsResult).ToList();
        if (resultSteps.Count != 1)
            result.Issues.Add(CreateIssue("result_step_count_invalid", "Exactly one low-level step must have isResult=true."));

        if (!string.IsNullOrWhiteSpace(plan.ResultStepId))
        {
            var resultStep = plan.Steps.FirstOrDefault(step => string.Equals(step.Id, plan.ResultStepId, StringComparison.OrdinalIgnoreCase));
            if (resultStep is null)
            {
                result.Issues.Add(CreateIssue("result_step_unknown", $"LowLevelPlan.resultStepId '{plan.ResultStepId}' does not exist."));
            }
            else
            {
                var hasDownstream = plan.Steps.Any(step =>
                    step.Inputs.Any(input =>
                        string.Equals(input.Source.StepId, resultStep.Id, StringComparison.OrdinalIgnoreCase)));
                if (hasDownstream)
                    result.Issues.Add(CreateIssue("result_step_non_terminal", $"Result step '{resultStep.Id}' must be terminal."));
            }
        }

        foreach (var step in plan.Steps)
        {
            if (step.IsResult)
                continue;

            var isUsed = plan.Steps.Any(candidate =>
                candidate.Inputs.Any(input =>
                    string.Equals(input.Source.StepId, step.Id, StringComparison.OrdinalIgnoreCase)));
            if (!isUsed)
                result.Issues.Add(CreateIssue("unused_step", $"Non-result step '{step.Id}' has no downstream consumer."));
        }

        if (result.Issues.Count == 0)
            AppendRuntimeCompileIssues(plan, tools, result);

        return result;
    }

    private static void ValidateInput(
        LowLevelPlan plan,
        LowLevelStep step,
        LowLevelStepInput input,
        int stepIndex,
        LowLevelValidationResult result)
    {
        if (input.Source is null || string.IsNullOrWhiteSpace(input.Source.Kind))
        {
            result.Issues.Add(CreateIssue("input_source_missing", $"Step '{step.Id}' input '{input.Name}' has no source."));
            return;
        }

        if (string.Equals(input.Source.Kind, LowLevelInputSourceKinds.Literal, StringComparison.OrdinalIgnoreCase))
            return;

        if (!string.Equals(input.Source.Kind, LowLevelInputSourceKinds.StepOutputPort, StringComparison.OrdinalIgnoreCase))
        {
            result.Issues.Add(CreateIssue("input_source_kind_invalid", $"Step '{step.Id}' input '{input.Name}' has unsupported source kind '{input.Source.Kind}'."));
            return;
        }

        if (string.IsNullOrWhiteSpace(input.Source.StepId) || string.IsNullOrWhiteSpace(input.Source.Port))
        {
            result.Issues.Add(CreateIssue("input_source_reference_missing", $"Step '{step.Id}' input '{input.Name}' must reference stepId and port."));
            return;
        }

        var sourceIndex = plan.Steps.FindIndex(candidate => string.Equals(candidate.Id, input.Source.StepId, StringComparison.OrdinalIgnoreCase));
        if (sourceIndex < 0)
        {
            result.Issues.Add(CreateIssue("input_source_step_missing", $"Step '{step.Id}' input '{input.Name}' references unknown step '{input.Source.StepId}'."));
            return;
        }

        if (sourceIndex >= stepIndex)
        {
            result.Issues.Add(CreateIssue("input_source_future_step", $"Step '{step.Id}' input '{input.Name}' references future step '{input.Source.StepId}'."));
            return;
        }

        var sourceStep = plan.Steps[sourceIndex];
        if (!sourceStep.Outputs.Any(output => string.Equals(output.Name, input.Source.Port, StringComparison.OrdinalIgnoreCase)))
        {
            result.Issues.Add(CreateIssue("input_source_port_missing", $"Step '{step.Id}' input '{input.Name}' references missing port '{input.Source.Port}' on step '{input.Source.StepId}'."));
            return;
        }

        var mode = string.IsNullOrWhiteSpace(input.Source.Mode) ? LowLevelInputModes.Value : input.Source.Mode;
        if (!string.Equals(mode, LowLevelInputModes.Value, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(mode, LowLevelInputModes.Map, StringComparison.OrdinalIgnoreCase))
        {
            result.Issues.Add(CreateIssue("input_mode_invalid", $"Step '{step.Id}' input '{input.Name}' has unsupported mode '{mode}'."));
        }
    }

    private static PlanningIssue CreateIssue(string code, string message) =>
        new()
        {
            Code = code,
            Message = message,
            Layer = "low_level"
        };

    private static void ValidateExecutionContract(
        LowLevelStep step,
        OutlineNode outlineNode,
        IReadOnlyDictionary<string, AppToolDescriptor> toolsById,
        string? resultStepId,
        LowLevelValidationResult result)
    {
        var contract = OutlineNodeExecutionContractResolver.Resolve(outlineNode.Kind);

        if (string.Equals(step.Kind, LowLevelStepKinds.Llm, StringComparison.OrdinalIgnoreCase))
        {
            if (!contract.AllowsLlm)
            {
                result.Issues.Add(CreateIssue(
                    "outline_execution_contract_mismatch",
                    $"Step '{step.Id}' is an llm step, but outline node '{outlineNode.Id}' with kind '{outlineNode.Kind}' requires {contract.Description}.",
                    new
                    {
                        stepId = step.Id,
                        outlineNodeId = outlineNode.Id,
                        outlineNodeKind = outlineNode.Kind,
                        capabilityId = step.CapabilityId,
                        expectedRole = contract.RequiredRole?.ToString(),
                        expectedProduces = contract.RequiredProduces?.ToString()
                    }));
            }

            if (contract.RequiresTerminalResult
                && !string.Equals(step.Id, resultStepId, StringComparison.OrdinalIgnoreCase)
                && !step.IsResult)
            {
                result.Issues.Add(CreateIssue(
                    "outline_terminal_result_mismatch",
                    $"Step '{step.Id}' materializes answer node '{outlineNode.Id}', but answer nodes must end at the terminal result step.",
                    new
                    {
                        stepId = step.Id,
                        outlineNodeId = outlineNode.Id,
                        outlineNodeKind = outlineNode.Kind,
                        resultStepId
                    }));
            }

            return;
        }

        if (!toolsById.TryGetValue(step.CapabilityId ?? string.Empty, out var tool))
            return;

        var actualRole = tool.PlanningMetadata?.PlannerRole;
        var actualProduces = tool.PlanningMetadata?.ProducesKind;
        var roleMismatch = contract.RequiredRole is not null && actualRole != contract.RequiredRole;
        var producesMismatch = contract.RequiredProduces is not null && actualProduces != contract.RequiredProduces;
        if (!roleMismatch && !producesMismatch && !contract.RequiresTerminalResult)
            return;

        result.Issues.Add(CreateIssue(
            "outline_execution_contract_mismatch",
            $"Step '{step.Id}' uses capability '{step.CapabilityId}', but outline node '{outlineNode.Id}' with kind '{outlineNode.Kind}' requires {contract.Description}",
            new
            {
                stepId = step.Id,
                outlineNodeId = outlineNode.Id,
                outlineNodeKind = outlineNode.Kind,
                capabilityId = step.CapabilityId,
                expectedRole = contract.RequiredRole?.ToString(),
                actualRole = actualRole?.ToString(),
                expectedProduces = contract.RequiredProduces?.ToString(),
                actualProduces = actualProduces?.ToString()
            }));
    }

    private static PlanningIssue CreateIssue(string code, string message, object details) =>
        new()
        {
            Code = code,
            Message = message,
            Layer = "low_level",
            Details = JsonSerializer.SerializeToElement(details)
        };

    private static void AppendRuntimeCompileIssues(
        LowLevelPlan plan,
        IReadOnlyCollection<AppToolDescriptor> tools,
        LowLevelValidationResult result)
    {
        var compileResult = new RuntimePlannerCompiler(tools).Compile(plan);
        if (compileResult.IsSuccess)
            return;

        foreach (var issue in compileResult.Issues)
        {
            if (result.Issues.Any(existing =>
                string.Equals(existing.Layer, issue.Layer, StringComparison.OrdinalIgnoreCase)
                && string.Equals(existing.Code, issue.Code, StringComparison.OrdinalIgnoreCase)
                && string.Equals(existing.Message, issue.Message, StringComparison.Ordinal)))
            {
                continue;
            }

            result.Issues.Add(new PlanningIssue
            {
                Layer = issue.Layer,
                Code = issue.Code,
                Message = issue.Message,
                Details = issue.Details is JsonElement details ? details.Clone() : null,
                IsBlocking = issue.IsBlocking
            });
        }
    }
}

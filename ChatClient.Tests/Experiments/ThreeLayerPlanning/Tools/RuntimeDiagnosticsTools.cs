using ChatClient.Api.Services;
using ChatClient.Tests.Experiments.ThreeLayerPlanning.Contracts;
using ChatClient.Tests.Experiments.ThreeLayerPlanning.Runtime;
using ChatClient.Tests.Experiments.ThreeLayerPlanning.Shared;

namespace ChatClient.Tests.Experiments.ThreeLayerPlanning.Tools;

public sealed class RuntimeDerivedContractDescriptor
{
    public string StepId { get; init; } = string.Empty;

    public string PortName { get; init; } = string.Empty;

    public string SemanticType { get; init; } = string.Empty;

    public string? Format { get; init; }
}

public sealed class RuntimeDiagnosticsTools
{
    private readonly IReadOnlyCollection<AppToolDescriptor> _tools;
    private RuntimeCompileResult? _lastCompile;

    public RuntimeDiagnosticsTools(IReadOnlyCollection<AppToolDescriptor> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        _tools = tools;
    }

    public RuntimeCompileResult Compile(LowLevelPlan lowLevelPlan)
    {
        var compiler = new RuntimePlannerCompiler(_tools);
        _lastCompile = compiler.Compile(lowLevelPlan);
        return _lastCompile;
    }

    public IReadOnlyList<ExperimentIssue> ReadCompileDiagnostics() =>
        _lastCompile?.Issues ?? [];

    public IReadOnlyList<RuntimeDerivedContractDescriptor> ReadDerivedContracts()
    {
        if (_lastCompile?.Plan is null)
            return [];

        return _lastCompile.Plan.Steps
            .SelectMany(step => step.Outputs.Select(output => new RuntimeDerivedContractDescriptor
            {
                StepId = step.Id,
                PortName = output.Name,
                SemanticType = output.SemanticType,
                Format = step.Out?.Format
            }))
            .ToList();
    }

    public RuntimeInputValue? ReadFailedBinding(string stepId, string inputName)
    {
        var step = _lastCompile?.Plan?.Steps
            .FirstOrDefault(candidate => string.Equals(candidate.Id, stepId, StringComparison.OrdinalIgnoreCase));
        if (step is not null && step.In.TryGetValue(inputName, out var input))
            return input;

        return null;
    }

    public RuntimePlanValidationResult Validate() =>
        _lastCompile?.Plan is null
            ? new RuntimePlanValidationResult()
            : RuntimePlanValidator.Validate(_lastCompile.Plan, _tools);
}

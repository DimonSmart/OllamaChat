using ChatClient.Tests.Experiments.ThreeLayerPlanning.Contracts;
using ChatClient.Tests.Experiments.ThreeLayerPlanning.Shared;

namespace ChatClient.Tests.Experiments.ThreeLayerPlanning.Runtime;

public static class RuntimeBindingResolver
{
    public static RuntimeInputValue Resolve(LowLevelInputSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (string.Equals(source.Kind, LowLevelInputSourceKinds.Literal, StringComparison.OrdinalIgnoreCase))
        {
            return new RuntimeInputValue
            {
                Kind = RuntimeInputValueKinds.Literal,
                Literal = ExperimentJson.CloneNode(source.Value)
            };
        }

        if (!string.Equals(source.Kind, LowLevelInputSourceKinds.StepOutputPort, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Unsupported low-level input source kind '{source.Kind}'.");

        return new RuntimeInputValue
        {
            Kind = RuntimeInputValueKinds.Binding,
            From = $"${source.StepId}.{source.Port}",
            Mode = string.IsNullOrWhiteSpace(source.Mode)
                ? LowLevelInputModes.Value
                : source.Mode
        };
    }

    public static bool TryParseBindingPath(string path, out string stepId, out string port)
    {
        stepId = string.Empty;
        port = string.Empty;
        if (string.IsNullOrWhiteSpace(path) || !path.StartsWith('$'))
            return false;

        var separatorIndex = path.IndexOf('.');
        if (separatorIndex <= 1 || separatorIndex == path.Length - 1)
            return false;

        stepId = path[1..separatorIndex];
        port = path[(separatorIndex + 1)..];
        return !string.IsNullOrWhiteSpace(stepId) && !string.IsNullOrWhiteSpace(port);
    }
}

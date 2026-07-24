using System.Globalization;

namespace ChatClient.Api.AgentWorkflows;

public sealed class WorkflowStartValues
{
    private readonly IReadOnlyDictionary<string, string> _values;

    public WorkflowStartValues(IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        _values = new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase);
    }

    public int RequireInt32(string key, int? min = null, int? max = null)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("Workflow start input key is required.", nameof(key));
        }

        var normalizedKey = key.Trim();
        if (!_values.TryGetValue(normalizedKey, out var rawValue) || string.IsNullOrWhiteSpace(rawValue))
        {
            throw new InvalidOperationException(
                $"Workflow start input '{normalizedKey}' requires a value.");
        }

        if (!int.TryParse(rawValue.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            throw new InvalidOperationException(
                $"Workflow start input '{normalizedKey}' expects a whole number.");
        }

        if (min.HasValue && value < min.Value)
        {
            throw CreateRangeException(normalizedKey, min, max);
        }

        if (max.HasValue && value > max.Value)
        {
            throw CreateRangeException(normalizedKey, min, max);
        }

        return value;
    }

    private static InvalidOperationException CreateRangeException(
        string key,
        int? min,
        int? max)
    {
        var constraint = (min, max) switch
        {
            ({ } minimum, { } maximum) => $"between {minimum} and {maximum}",
            ({ } minimum, null) => $"at least {minimum}",
            (null, { } maximum) => $"at most {maximum}",
            _ => throw new InvalidOperationException("A numeric range constraint is required.")
        };

        return new InvalidOperationException(
            $"Workflow start input '{key}' must be {constraint}.");
    }
}

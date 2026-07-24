using System.Globalization;

namespace ChatClient.Api.AgentWorkflows;

public sealed class WorkflowStartValues
{
    private readonly IReadOnlyDictionary<string, string> _values;

    public WorkflowStartValues(IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        _values = values.ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value,
            StringComparer.OrdinalIgnoreCase);
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
        string constraint;
        if (min.HasValue && max.HasValue)
        {
            constraint = $"between {min.Value} and {max.Value}";
        }
        else if (min.HasValue)
        {
            constraint = $"at least {min.Value}";
        }
        else if (max.HasValue)
        {
            constraint = $"at most {max.Value}";
        }
        else
        {
            throw new InvalidOperationException("A numeric range constraint is required.");
        }

        return new InvalidOperationException(
            $"Workflow start input '{key}' must be {constraint}.");
    }
}

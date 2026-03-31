namespace ChatClient.Tests;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class RealWorkflowFactAttribute : FactAttribute
{
    public const string EnableEnvironmentVariable = "CHATCLIENT_RUN_REAL_WORKFLOW_TESTS";

    public RealWorkflowFactAttribute()
    {
        Skip = IsEnabled()
            ? null
            : $"Set {EnableEnvironmentVariable}=1 to run real workflow tests.";
    }

    internal static bool IsEnabled()
    {
        var value = Environment.GetEnvironmentVariable(EnableEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.Trim() switch
        {
            "1" => true,
            "true" => true,
            "TRUE" => true,
            "yes" => true,
            "YES" => true,
            _ => bool.TryParse(value, out var parsed) && parsed
        };
    }
}

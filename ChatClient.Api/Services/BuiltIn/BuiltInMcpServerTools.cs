using ModelContextProtocol.Server;
using System.ComponentModel;

namespace ChatClient.Api.Services.BuiltIn;

[McpServerToolType]
public sealed class BuiltInTimeServerTools
{
    public static IBuiltInMcpServerDescriptor Descriptor { get; } = new BuiltInMcpServerDescriptor(
        id: Guid.Parse("f2f13fdb-09e4-46b8-9e2e-352c3da66f20"),
        key: "built-in-time",
        name: "Built-in Time MCP Server",
        description: "Returns current time information.",
        registerTools: static builder => builder.WithTools<BuiltInTimeServerTools>());

    [McpServerTool(Name = "get_current_time"), Description("Returns current time details in ISO-8601 format.")]
    public static object GetCurrentTime(
        [Description("Optional time zone ID. Supports both IANA and Windows IDs.")] string? timeZone = null)
    {
        var zone = BuiltInTimeZoneResolver.ResolveOrUtc(timeZone);
        var now = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone);

        return new
        {
            timeZone = zone.Id,
            isoTime = now.ToString("O"),
            unixSeconds = now.ToUnixTimeSeconds()
        };
    }
}

[McpServerToolType]
public sealed class BuiltInMathServerTools
{
    public static IBuiltInMcpServerDescriptor Descriptor { get; } = new BuiltInMcpServerDescriptor(
        id: Guid.Parse("76ca15c0-4f2d-4a76-8d32-70fdd6dd5083"),
        key: "built-in-math",
        name: "Built-in Math MCP Server",
        description: "Evaluates arithmetic expressions from text input.",
        registerTools: static builder => builder.WithTools<BuiltInMathServerTools>());

    [McpServerTool(Name = "evaluate_expression"), Description("Evaluates an arithmetic expression string and returns the numeric result.")]
    public static object EvaluateExpression(
        [Description("Expression with numbers, parentheses and operators + - * / % ^")] string expression)
    {
        var result = MathExpressionEvaluator.Evaluate(expression);

        return new
        {
            expression,
            result
        };
    }
}

internal static class BuiltInTimeZoneResolver
{
    public static TimeZoneInfo ResolveOrUtc(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return TimeZoneInfo.Utc;

        var trimmed = id.Trim();
        if (TryFindTimeZone(trimmed, out var zone))
            return zone;

        throw new InvalidOperationException($"Unknown time zone '{trimmed}'.");
    }

    private static bool TryFindTimeZone(string id, out TimeZoneInfo timeZoneInfo)
    {
        try
        {
            timeZoneInfo = TimeZoneInfo.FindSystemTimeZoneById(id);
            return true;
        }
        catch
        {
            // Ignore and continue with conversion attempts.
        }

        if (TimeZoneInfo.TryConvertIanaIdToWindowsId(id, out var windowsId))
        {
            try
            {
                timeZoneInfo = TimeZoneInfo.FindSystemTimeZoneById(windowsId);
                return true;
            }
            catch
            {
                // Ignore and continue with conversion attempts.
            }
        }

        if (TimeZoneInfo.TryConvertWindowsIdToIanaId(id, out var ianaId))
        {
            try
            {
                timeZoneInfo = TimeZoneInfo.FindSystemTimeZoneById(ianaId);
                return true;
            }
            catch
            {
                // Ignore and continue with fallback.
            }
        }

        timeZoneInfo = TimeZoneInfo.Utc;
        return false;
    }
}

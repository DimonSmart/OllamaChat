using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

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
public sealed class BuiltInFormattedTimeServerTools
{
    public static IBuiltInMcpServerDescriptor Descriptor { get; } = new BuiltInMcpServerDescriptor(
        id: Guid.Parse("1b44ff82-c4fc-4f50-a12f-56429817c078"),
        key: "built-in-formatted-time",
        name: "Built-in Formatted Time MCP Server",
        description: "Returns current time in a custom format and asks user for timezone when needed.",
        registerTools: static builder => builder.WithTools<BuiltInFormattedTimeServerTools>());

    [McpServerTool(Name = "get_formatted_time"), Description("Returns current time formatted with a .NET format string. If timezone is omitted, asks user via elicitation.")]
    public static async Task<object> GetFormattedTimeAsync(
        McpServer server,
        [Description("Date/time format string, e.g. yyyy-MM-dd HH:mm:ss zzz")] string format = "yyyy-MM-dd HH:mm:ss zzz",
        [Description("Optional time zone ID. If omitted, the server asks the user.")] string? timeZone = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(format))
            throw new InvalidOperationException("Format cannot be empty.");

        var effectiveTimeZone = string.IsNullOrWhiteSpace(timeZone)
            ? await PromptForTimeZoneAsync(server, cancellationToken)
            : timeZone.Trim();

        var zone = BuiltInTimeZoneResolver.ResolveOrUtc(effectiveTimeZone);
        var now = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone);

        string formatted;
        try
        {
            formatted = now.ToString(format, CultureInfo.InvariantCulture);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException($"Invalid date/time format: {format}", ex);
        }

        return new
        {
            timeZone = zone.Id,
            format,
            formatted,
            isoTime = now.ToString("O")
        };
    }

    private static async Task<string> PromptForTimeZoneAsync(McpServer server, CancellationToken cancellationToken)
    {
        var request = new ElicitRequestParams
        {
            Mode = "form",
            Message = "Specify a time zone (for example: UTC, Europe/Berlin, America/New_York, Pacific Standard Time).",
            RequestedSchema = new ElicitRequestParams.RequestSchema
            {
                Properties = new Dictionary<string, ElicitRequestParams.PrimitiveSchemaDefinition>(StringComparer.Ordinal)
                {
                    ["timeZone"] = new ElicitRequestParams.StringSchema
                    {
                        Type = "string",
                        Title = "Time zone",
                        Description = "Time zone ID in IANA or Windows format."
                    }
                },
                Required = ["timeZone"]
            }
        };

        var response = await server.ElicitAsync(request, cancellationToken);
        if (!response.IsAccepted || response.Content is null || !response.Content.TryGetValue("timeZone", out var value))
            throw new InvalidOperationException("Time zone was not provided by the user.");

        var selected = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => value.GetRawText()
        };

        if (string.IsNullOrWhiteSpace(selected))
            throw new InvalidOperationException("Time zone was not provided by the user.");

        return selected.Trim();
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

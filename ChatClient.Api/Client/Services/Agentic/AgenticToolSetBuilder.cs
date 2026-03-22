using System.Text;
using System.Text.Json;
using ChatClient.Api.Services;
using ChatClient.Domain.Models;
using Microsoft.Extensions.AI;

namespace ChatClient.Api.Client.Services.Agentic;

internal sealed record AgenticRegisteredTool(
    string RegisteredName,
    string ServerName,
    string ToolName,
    bool MayRequireUserInput,
    AITool Tool);

internal sealed record AgenticToolSet(
    IReadOnlyList<AITool> Tools,
    IReadOnlyDictionary<string, AgenticRegisteredTool> MetadataByName)
{
    public static AgenticToolSet Empty { get; } =
        new([], new Dictionary<string, AgenticRegisteredTool>(StringComparer.OrdinalIgnoreCase));

    public bool HasTools => Tools.Count > 0;
}

internal static class AgenticToolSetBuilder
{
    private static readonly JsonElement EmptyObjectSchema = CreateEmptyObjectSchema();

    public static AgenticToolSet Build(
        IReadOnlyCollection<string> requestedFunctions,
        IReadOnlyCollection<AppToolDescriptor> availableTools,
        WhiteboardState? whiteboard,
        bool useWhiteboard)
    {
        List<AgenticToolSpec> specs = [];

        if (requestedFunctions.Count > 0)
        {
            var exactRequested = new HashSet<string>(
                requestedFunctions
                    .Where(ContainsQualifier)
                    .Select(static value => value.Trim()),
                StringComparer.OrdinalIgnoreCase);

            var shortRequested = new HashSet<string>(
                requestedFunctions
                    .Where(static value => !ContainsQualifier(value))
                    .Select(static value => value.Trim()),
                StringComparer.OrdinalIgnoreCase);

            foreach (var tool in availableTools)
            {
                bool isSelected = exactRequested.Contains(tool.QualifiedName) ||
                                  shortRequested.Contains(tool.ToolName);
                if (!isSelected)
                {
                    continue;
                }

                specs.Add(AgenticToolSpec.FromAppTool(tool));
            }
        }

        if (useWhiteboard && whiteboard is not null)
        {
            specs.AddRange(BuildWhiteboardSpecs(whiteboard));
        }

        if (specs.Count == 0)
        {
            return AgenticToolSet.Empty;
        }

        var registeredNames = AssignRegisteredNames(specs);
        List<AITool> tools = [];
        Dictionary<string, AgenticRegisteredTool> metadataByName = new(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < specs.Count; i++)
        {
            var spec = specs[i];
            var registeredName = registeredNames[i];
            var description = BuildRegisteredDescription(spec, registeredName);
            var tool = BuildTool(spec, registeredName, description);

            tools.Add(tool);
            metadataByName[registeredName] = new AgenticRegisteredTool(
                registeredName,
                spec.ServerName,
                spec.ToolName,
                spec.MayRequireUserInput,
                tool);
        }

        return new AgenticToolSet(tools, metadataByName);
    }

    private static AITool BuildTool(AgenticToolSpec spec, string registeredName, string description)
    {
        var schema = spec.InputSchema.ValueKind == JsonValueKind.Undefined
            ? EmptyObjectSchema
            : spec.InputSchema.Clone();
        var innerFunction = AIFunctionFactory.Create(
            (AIFunctionArguments arguments, CancellationToken cancellationToken) =>
                spec.ExecuteAsync(ToDictionary(arguments), cancellationToken),
            new AIFunctionFactoryOptions
            {
                Name = registeredName,
                Description = description,
                ExcludeResultSchema = spec.OutputSchema is null
            });

        return new ConfiguredAgenticFunction(
            innerFunction,
            registeredName,
            description,
            schema,
            spec.OutputSchema);
    }

    private static Dictionary<string, object?> ToDictionary(AIFunctionArguments arguments)
    {
        Dictionary<string, object?> result = new(StringComparer.OrdinalIgnoreCase);

        foreach (var pair in arguments)
        {
            result[pair.Key] = pair.Value;
        }

        return result;
    }

    private static IReadOnlyList<string> AssignRegisteredNames(IReadOnlyList<AgenticToolSpec> specs)
    {
        Dictionary<string, int> countsByToolName = new(StringComparer.OrdinalIgnoreCase);
        foreach (var spec in specs)
        {
            countsByToolName[spec.ToolName] = countsByToolName.GetValueOrDefault(spec.ToolName) + 1;
        }

        HashSet<string> usedNames = new(StringComparer.OrdinalIgnoreCase);
        List<string> names = [];

        foreach (var spec in specs)
        {
            if (countsByToolName[spec.ToolName] == 1 && usedNames.Add(spec.ToolName))
            {
                names.Add(spec.ToolName);
                continue;
            }

            names.Add(CreateDisambiguatedToolName(spec.SourceLabel, spec.ToolName, usedNames));
        }

        return names;
    }

    private static string BuildRegisteredDescription(AgenticToolSpec spec, string registeredName)
    {
        var description = string.IsNullOrWhiteSpace(spec.Description)
            ? $"Tool from {spec.ServerName}."
            : spec.Description.Trim();

        if (string.Equals(registeredName, spec.ToolName, StringComparison.OrdinalIgnoreCase))
        {
            return description;
        }

        return $"{description}\n\nSource server: {spec.ServerName}. Original tool name: {spec.ToolName}.";
    }

    private static IReadOnlyList<AgenticToolSpec> BuildWhiteboardSpecs(WhiteboardState whiteboard)
    {
        var addNoteSchema = AgenticToolUtility.ParseToolSchema("""
            {
              "type": "object",
              "properties": {
                "note": { "type": "string" },
                "author": { "type": ["string", "null"] }
              },
              "required": ["note"],
              "additionalProperties": false
            }
            """);

        var emptySchema = AgenticToolUtility.ParseToolSchema("""
            {
              "type": "object",
              "properties": {},
              "additionalProperties": false
            }
            """);

        var stringSchema = AgenticToolUtility.ParseToolSchema("""
            {
              "type": "string"
            }
            """);

        return
        [
            new AgenticToolSpec(
                "whiteboard",
                "whiteboard",
                "add_note",
                "Add or update a note on the shared whiteboard for this chat session.",
                addNoteSchema,
                stringSchema,
                false,
                (arguments, _) =>
                {
                    var note = AgenticToolUtility.ReadRequiredStringArgument(arguments, "note");
                    var author = AgenticToolUtility.ReadOptionalStringArgument(arguments, "author");
                    whiteboard.Add(note, author);
                    return Task.FromResult<object>(AgenticToolUtility.BuildWhiteboardSnapshot(whiteboard));
                }),
            new AgenticToolSpec(
                "whiteboard",
                "whiteboard",
                "get_notes",
                "Return all whiteboard notes as a markdown list.",
                emptySchema,
                stringSchema,
                false,
                (_, _) => Task.FromResult<object>(AgenticToolUtility.BuildWhiteboardSnapshot(whiteboard))),
            new AgenticToolSpec(
                "whiteboard",
                "whiteboard",
                "clear",
                "Clear every note from the shared whiteboard.",
                emptySchema,
                stringSchema,
                false,
                (_, _) =>
                {
                    whiteboard.Clear();
                    return Task.FromResult<object>("Whiteboard cleared.");
                })
        ];
    }

    private static bool ContainsQualifier(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Contains(':', StringComparison.Ordinal);

    private static string CreateDisambiguatedToolName(
        string sourceLabel,
        string toolName,
        HashSet<string> usedNames)
    {
        const int maxLength = 64;
        var baseName = $"{SanitizeToolNamePart(sourceLabel)}__{SanitizeToolNamePart(toolName)}";
        if (baseName.Length > maxLength)
        {
            baseName = baseName[..maxLength];
        }

        var candidate = baseName;
        var suffix = 1;
        while (!usedNames.Add(candidate))
        {
            var suffixText = $"_{suffix++}";
            var prefixLength = Math.Max(1, maxLength - suffixText.Length);
            candidate = $"{baseName[..Math.Min(baseName.Length, prefixLength)]}{suffixText}";
        }

        return candidate;
    }

    private static string SanitizeToolNamePart(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "tool";
        }

        StringBuilder builder = new(value.Length);
        foreach (var ch in value)
        {
            builder.Append(char.IsLetterOrDigit(ch) || ch is '_' or '-' ? ch : '_');
        }

        return builder.Length == 0 ? "tool" : builder.ToString();
    }

    private static JsonElement CreateEmptyObjectSchema()
    {
        using var document = JsonDocument.Parse("{\"type\":\"object\",\"properties\":{}}");
        return document.RootElement.Clone();
    }

    private sealed record AgenticToolSpec(
        string ServerName,
        string SourceLabel,
        string ToolName,
        string Description,
        JsonElement InputSchema,
        JsonElement? OutputSchema,
        bool MayRequireUserInput,
        Func<Dictionary<string, object?>, CancellationToken, Task<object>> ExecuteAsync)
    {
        public static AgenticToolSpec FromAppTool(AppToolDescriptor tool)
        {
            var inputSchema = tool.InputSchema.ValueKind == JsonValueKind.Undefined
                ? EmptyObjectSchema.Clone()
                : tool.InputSchema.Clone();
            JsonElement? outputSchema = tool.OutputSchema is { } schema &&
                                        schema.ValueKind != JsonValueKind.Undefined
                ? schema.Clone()
                : (JsonElement?)null;
            var sourceLabel = string.IsNullOrWhiteSpace(tool.BindingDisplayName)
                ? tool.BaseServerName ?? tool.ServerName
                : tool.BindingDisplayName.Trim();

            return new AgenticToolSpec(
                tool.ServerName,
                sourceLabel,
                tool.ToolName,
                tool.Description,
                inputSchema,
                outputSchema,
                tool.MayRequireUserInput,
                tool.ExecuteAsync);
        }
    }
}

internal sealed class ConfiguredAgenticFunction(
    AIFunction innerFunction,
    string name,
    string description,
    JsonElement jsonSchema,
    JsonElement? returnJsonSchema) : DelegatingAIFunction(innerFunction)
{
    private readonly JsonElement _jsonSchema = jsonSchema.Clone();
    private readonly JsonElement? _returnJsonSchema = returnJsonSchema?.Clone();

    public override string Name => name;

    public override string Description => description;

    public override JsonElement JsonSchema => _jsonSchema;

    public override JsonElement? ReturnJsonSchema => _returnJsonSchema;
}

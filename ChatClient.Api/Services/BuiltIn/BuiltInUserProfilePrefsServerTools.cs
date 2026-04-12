using System.Reflection;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ChatClient.Api.Services.BuiltIn;

public sealed class BuiltInUserProfilePrefsServerTools
{
    private const int MaxElicitationAttempts = 3;
    private const string ValueFieldName = "value";
    private const string StoredSource = "stored";
    private const string ElicitedSource = "elicited";

    public static IBuiltInMcpServerDescriptor Descriptor { get; } = new BuiltInMcpServerDescriptor(
        id: Guid.Parse("c8c4a3cf-e2d5-4f4d-9a6f-4504e322a2b3"),
        key: "built-in-user-profile-prefs",
        name: "Built-in User Profile Prefs MCP Server",
        description: "Stores and retrieves configured user profile preferences for personalization.",
        registerTools: static builder => builder.WithTools(CreateTools()),
        descriptionFactory: static () => UserProfilePreferencesRuntime.BuildServerDescription(UserProfilePreferencesStore.GetSnapshot()));

    private static IEnumerable<McpServerTool> CreateTools()
    {
        var snapshot = UserProfilePreferencesRuntime.CreateSnapshot(UserProfilePreferencesStore.GetSnapshot());
        var invokerType = typeof(ToolInvoker);

        var prefsGetTool = McpServerTool.Create(
            invokerType.GetMethod(nameof(ToolInvoker.PrefsGetAsync), BindingFlags.Instance | BindingFlags.Public)!,
            static _ => new ToolInvoker(),
            new McpServerToolCreateOptions
            {
                Name = "prefs_get",
                Description = UserProfilePreferencesRuntime.BuildPrefsGetDescription(snapshot),
                UseStructuredContent = true
            });
        prefsGetTool.ProtocolTool.InputSchema = UserProfilePreferencesRuntime.BuildPrefsGetInputSchema(snapshot);
        prefsGetTool.ProtocolTool.OutputSchema = UserProfilePreferencesRuntime.BuildPrefsGetOutputSchema();

        var prefsGetAllTool = McpServerTool.Create(
            invokerType.GetMethod(nameof(ToolInvoker.PrefsGetAllAsync), BindingFlags.Instance | BindingFlags.Public)!,
            static _ => new ToolInvoker(),
            new McpServerToolCreateOptions
            {
                Name = "prefs_get_all",
                Description = UserProfilePreferencesRuntime.BuildPrefsGetAllDescription(snapshot),
                ReadOnly = true,
                UseStructuredContent = true
            });
        prefsGetAllTool.ProtocolTool.OutputSchema = UserProfilePreferencesRuntime.BuildPrefsGetAllOutputSchema();

        var prefsResetAllTool = McpServerTool.Create(
            invokerType.GetMethod(nameof(ToolInvoker.PrefsResetAllAsync), BindingFlags.Instance | BindingFlags.Public)!,
            static _ => new ToolInvoker(),
            new McpServerToolCreateOptions
            {
                Name = "prefs_reset_all",
                Description = UserProfilePreferencesRuntime.BuildPrefsResetAllDescription(),
                UseStructuredContent = true
            });
        prefsResetAllTool.ProtocolTool.InputSchema = UserProfilePreferencesRuntime.BuildPrefsResetAllInputSchema();
        prefsResetAllTool.ProtocolTool.OutputSchema = UserProfilePreferencesRuntime.BuildPrefsResetAllOutputSchema();

        return [prefsGetTool, prefsGetAllTool, prefsResetAllTool];
    }

    public sealed class ToolInvoker
    {
        public async Task<object> PrefsGetAsync(
            McpServer server,
            string key,
            CancellationToken cancellationToken = default)
        {
            var document = await UserProfilePreferencesStore.GetAsync(cancellationToken);
            var snapshot = UserProfilePreferencesRuntime.CreateSnapshot(document);
            if (!snapshot.TryResolveKey(key, out var normalizedKey) ||
                !snapshot.TryGetDefinition(normalizedKey, out var definition))
            {
                throw new InvalidOperationException($"unknown_key:{key?.Trim() ?? string.Empty}");
            }

            if (snapshot.TryGetStoredValue(normalizedKey, out var storedValue))
            {
                return new
                {
                    key = normalizedKey,
                    exists = true,
                    value = storedValue,
                    source = StoredSource
                };
            }

            var elicitedValue = await ElicitPreferenceValueAsync(server, definition, cancellationToken);
            await UserProfilePreferencesStore.SetValueAsync(normalizedKey, elicitedValue, cancellationToken);

            return new
            {
                key = normalizedKey,
                exists = true,
                value = elicitedValue,
                source = ElicitedSource
            };
        }

        public async Task<object> PrefsGetAllAsync(CancellationToken cancellationToken = default)
        {
            var document = await UserProfilePreferencesStore.GetAsync(cancellationToken);
            var snapshot = UserProfilePreferencesRuntime.CreateSnapshot(document);

            return new
            {
                serverDescription = UserProfilePreferencesRuntime.BuildServerDescription(snapshot),
                supportedKeys = snapshot.SupportedKeys,
                acceptedKeys = snapshot.AcceptedKeys,
                definitions = snapshot.Definitions.Select(static definition => new
                {
                    key = definition.Key,
                    description = definition.Description,
                    prompt = definition.Prompt,
                    defaultValue = definition.DefaultValue,
                    allowedValues = definition.AllowedValues,
                    aliases = definition.Aliases
                }),
                values = snapshot.Values
            };
        }

        public async Task<object> PrefsResetAllAsync(
            McpServer server,
            bool confirm = false,
            CancellationToken cancellationToken = default)
        {
            var shouldReset = confirm || await ConfirmResetAsync(server, cancellationToken);
            if (!shouldReset)
            {
                return new
                {
                    cleared = false
                };
            }

            await UserProfilePreferencesStore.ClearValuesAsync(cancellationToken);
            return new
            {
                cleared = true
            };
        }
    }

    private static async Task<string> ElicitPreferenceValueAsync(
        McpServer server,
        UserProfilePreferenceDefinition definition,
        CancellationToken cancellationToken)
    {
        string? validationMessage = null;

        for (var attempt = 0; attempt < MaxElicitationAttempts; attempt++)
        {
            var request = BuildPreferenceElicitationRequest(definition, validationMessage);
            var response = await server.ElicitAsync(request, cancellationToken);

            if (!response.IsAccepted)
            {
                throw new InvalidOperationException("user_canceled");
            }

            var snapshot = UserProfilePreferencesRuntime.CreateSnapshot(UserProfilePreferencesStore.GetSnapshot());
            if (TryReadContentValue(response, ValueFieldName, out var rawValue) &&
                snapshot.TryNormalizeValue(definition.Key, rawValue, out var normalizedValue))
            {
                return normalizedValue;
            }

            validationMessage = definition.AllowedValues.Count > 0
                ? "Choose one of the suggested values."
                : "Value must not be empty.";
        }

        throw new InvalidOperationException("invalid_value");
    }

    private static ElicitRequestParams BuildPreferenceElicitationRequest(
        UserProfilePreferenceDefinition definition,
        string? validationMessage)
    {
        var message = string.IsNullOrWhiteSpace(validationMessage)
            ? definition.Prompt
            : $"{validationMessage} {definition.Prompt}";

        return new ElicitRequestParams
        {
            Mode = "form",
            Message = message,
            RequestedSchema = new ElicitRequestParams.RequestSchema
            {
                Properties = new Dictionary<string, ElicitRequestParams.PrimitiveSchemaDefinition>(StringComparer.Ordinal)
                {
                    [ValueFieldName] = BuildPreferenceSchema(definition)
                },
                Required = [ValueFieldName]
            }
        };
    }

    private static ElicitRequestParams.PrimitiveSchemaDefinition BuildPreferenceSchema(
        UserProfilePreferenceDefinition definition)
    {
        if (definition.AllowedValues.Count > 0)
        {
            return new ElicitRequestParams.TitledSingleSelectEnumSchema
            {
                Type = "string",
                Title = definition.Key,
                Description = definition.Description,
                OneOf = definition.AllowedValues
                    .Select(static value => new ElicitRequestParams.EnumSchemaOption
                    {
                        Const = value,
                        Title = value
                    })
                    .ToArray(),
                Default = definition.DefaultValue
            };
        }

        return new ElicitRequestParams.StringSchema
        {
            Type = "string",
            Title = definition.Key,
            Description = definition.Description,
            Default = definition.DefaultValue
        };
    }

    private static async Task<bool> ConfirmResetAsync(McpServer server, CancellationToken cancellationToken)
    {
        var request = new ElicitRequestParams
        {
            Mode = "form",
            Message = "Confirm resetting all saved user profile values?",
            RequestedSchema = new ElicitRequestParams.RequestSchema
            {
                Properties = new Dictionary<string, ElicitRequestParams.PrimitiveSchemaDefinition>(StringComparer.Ordinal)
                {
                    ["confirm"] = new ElicitRequestParams.TitledSingleSelectEnumSchema
                    {
                        Type = "string",
                        Title = "Confirmation",
                        OneOf =
                        [
                            new ElicitRequestParams.EnumSchemaOption { Const = "yes", Title = "Yes" },
                            new ElicitRequestParams.EnumSchemaOption { Const = "no", Title = "No" }
                        ],
                        Default = "no"
                    }
                },
                Required = ["confirm"]
            }
        };

        var response = await server.ElicitAsync(request, cancellationToken);
        if (!response.IsAccepted)
        {
            return false;
        }

        if (!TryReadContentValue(response, "confirm", out var decision))
        {
            return false;
        }

        return string.Equals(decision, "yes", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryReadContentValue(ElicitResult response, string key, out string value)
    {
        value = string.Empty;
        if (response.Content is null || !response.Content.TryGetValue(key, out var jsonValue))
        {
            return false;
        }

        value = jsonValue.ValueKind switch
        {
            JsonValueKind.String => jsonValue.GetString() ?? string.Empty,
            JsonValueKind.Number => jsonValue.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
            _ => jsonValue.GetRawText()
        };

        return true;
    }
}

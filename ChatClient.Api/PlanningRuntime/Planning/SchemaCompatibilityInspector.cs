using System.Text.Json;

namespace ChatClient.Api.PlanningRuntime.Planning;

internal static class SchemaCompatibilityInspector
{
    public static IReadOnlyList<string> ValidateCompatibility(
        JsonElement sourceSchema,
        JsonElement targetSchema,
        string path)
    {
        var issues = new List<string>();
        ValidateSchemaCompatibility(sourceSchema, targetSchema, path, issues);
        return issues;
    }

    public static string DescribeSchemaShape(JsonElement schema)
    {
        if (!PlanStepOutputContractResolver.TryGetSchemaTypes(schema, out var types) || types.Count == 0)
            return "unknown";

        var nonNullTypes = types
            .Where(type => !string.Equals(type, "null", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var isNullable = nonNullTypes.Count != types.Count;

        string shape;
        if (nonNullTypes.Count == 0)
        {
            shape = "null";
        }
        else if (nonNullTypes.Count == 1)
        {
            shape = nonNullTypes[0];
            if (string.Equals(shape, "array", StringComparison.Ordinal)
                && schema.TryGetProperty("items", out var itemsSchema))
            {
                shape = $"array<{DescribeSchemaShape(itemsSchema)}>";
            }
        }
        else
        {
            shape = string.Join("|", nonNullTypes);
        }

        return isNullable && !string.Equals(shape, "null", StringComparison.Ordinal)
            ? $"{shape}|null"
            : shape;
    }

    private static void ValidateSchemaCompatibility(
        JsonElement sourceSchema,
        JsonElement targetSchema,
        string path,
        List<string> issues)
    {
        if (TryGetCompositeVariants(targetSchema, "oneOf", out var oneOfVariants)
            || TryGetCompositeVariants(targetSchema, "anyOf", out oneOfVariants))
        {
            foreach (var variant in oneOfVariants)
            {
                var variantIssues = new List<string>();
                ValidateSchemaCompatibility(sourceSchema, variant, path, variantIssues);
                if (variantIssues.Count == 0)
                    return;
            }

            issues.Add($"Source schema at '{path}' does not satisfy any allowed input schema alternative.");
            return;
        }

        if (!PlanStepOutputContractResolver.TryGetSchemaTypes(sourceSchema, out var sourceTypes)
            || !PlanStepOutputContractResolver.TryGetSchemaTypes(targetSchema, out var targetTypes))
        {
            return;
        }

        if (sourceTypes.Contains("null", StringComparer.Ordinal)
            && !targetTypes.Contains("null", StringComparer.Ordinal))
        {
            issues.Add($"Input '{path}' may resolve to null, but the target tool input requires a non-null value.");
            return;
        }

        var nonNullSourceTypes = sourceTypes
            .Where(type => !string.Equals(type, "null", StringComparison.Ordinal))
            .ToList();
        if (nonNullSourceTypes.Count == 0)
            return;

        var incompatibleSourceTypes = nonNullSourceTypes
            .Where(sourceType => !TargetAcceptsSourceType(targetTypes, sourceType))
            .ToList();
        if (incompatibleSourceTypes.Count > 0)
        {
            issues.Add($"Input '{path}' expects {string.Join("|", targetTypes)}, but the bound source schema produces {string.Join("|", incompatibleSourceTypes)}.");
            return;
        }

        if (targetTypes.Contains("object", StringComparer.Ordinal)
            && nonNullSourceTypes.Contains("object", StringComparer.Ordinal))
        {
            ValidateObjectSchemaCompatibility(sourceSchema, targetSchema, path, issues);
        }

        if (targetTypes.Contains("array", StringComparer.Ordinal)
            && nonNullSourceTypes.Contains("array", StringComparer.Ordinal)
            && sourceSchema.TryGetProperty("items", out var sourceItems)
            && targetSchema.TryGetProperty("items", out var targetItems))
        {
            ValidateSchemaCompatibility(sourceItems, targetItems, $"{path}[]", issues);
        }
    }

    private static void ValidateObjectSchemaCompatibility(
        JsonElement sourceSchema,
        JsonElement targetSchema,
        string path,
        List<string> issues)
    {
        if (!targetSchema.TryGetProperty("required", out var requiredElement)
            || requiredElement.ValueKind != JsonValueKind.Array
            || !TryGetPropertySchemas(sourceSchema, out var sourceProperties)
            || !TryGetPropertySchemas(targetSchema, out var targetProperties))
        {
            return;
        }

        foreach (var requiredProperty in requiredElement.EnumerateArray())
        {
            var propertyName = requiredProperty.GetString();
            if (string.IsNullOrWhiteSpace(propertyName))
                continue;

            if (!targetProperties.TryGetValue(propertyName, out var targetPropertySchema))
                continue;

            if (!sourceProperties.TryGetValue(propertyName, out var sourcePropertySchema))
            {
                issues.Add($"Required property '{propertyName}' is not guaranteed by the bound source schema at '{path}'.");
                continue;
            }

            ValidateSchemaCompatibility(sourcePropertySchema, targetPropertySchema, $"{path}.{propertyName}", issues);
        }
    }

    private static bool TryGetPropertySchemas(
        JsonElement schema,
        out IReadOnlyDictionary<string, JsonElement> properties)
    {
        if (schema.TryGetProperty("properties", out var propertiesElement)
            && propertiesElement.ValueKind == JsonValueKind.Object)
        {
            properties = propertiesElement.EnumerateObject()
                .ToDictionary(property => property.Name, property => property.Value.Clone(), StringComparer.OrdinalIgnoreCase);
            return true;
        }

        properties = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        return false;
    }

    private static bool TryGetCompositeVariants(
        JsonElement schema,
        string propertyName,
        out IReadOnlyList<JsonElement> variants)
    {
        variants = Array.Empty<JsonElement>();
        if (!schema.TryGetProperty(propertyName, out var variantsElement)
            || variantsElement.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        variants = variantsElement
            .EnumerateArray()
            .Where(static item => item.ValueKind == JsonValueKind.Object)
            .Select(static item => item.Clone())
            .ToArray();

        return variants.Count > 0;
    }

    private static bool TargetAcceptsSourceType(IReadOnlyList<string> targetTypes, string sourceType) =>
        targetTypes.Contains(sourceType, StringComparer.Ordinal)
        || (string.Equals(sourceType, "integer", StringComparison.Ordinal)
            && targetTypes.Contains("number", StringComparer.Ordinal));
}

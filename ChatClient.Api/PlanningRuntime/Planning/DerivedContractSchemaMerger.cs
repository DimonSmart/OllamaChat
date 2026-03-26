using System.Text.Json;
using System.Text.Json.Nodes;

namespace ChatClient.Api.PlanningRuntime.Planning;

internal static class DerivedContractSchemaMerger
{
    public static bool TryMergeAll(
        IReadOnlyCollection<JsonElement> schemas,
        out JsonElement mergedSchema)
    {
        mergedSchema = default;
        if (schemas.Count == 0)
            return false;

        using var enumerator = schemas.GetEnumerator();
        enumerator.MoveNext();
        mergedSchema = enumerator.Current.Clone();

        while (enumerator.MoveNext())
        {
            if (!TryMerge(mergedSchema, enumerator.Current, out mergedSchema))
                return false;
        }

        return true;
    }

    public static bool TryMerge(
        JsonElement left,
        JsonElement right,
        out JsonElement mergedSchema)
    {
        mergedSchema = default;

        if (IsOpaqueSchema(left))
        {
            mergedSchema = right.Clone();
            return true;
        }

        if (IsOpaqueSchema(right))
        {
            mergedSchema = left.Clone();
            return true;
        }

        if (string.Equals(left.GetRawText(), right.GetRawText(), StringComparison.Ordinal))
        {
            mergedSchema = left.Clone();
            return true;
        }

        if (!PlanStepOutputContractResolver.TryGetSchemaTypes(left, out var leftTypes)
            || !PlanStepOutputContractResolver.TryGetSchemaTypes(right, out var rightTypes))
        {
            return false;
        }

        var mergedTypes = IntersectTypes(leftTypes, rightTypes);
        if (mergedTypes.Count == 0)
            return false;

        if (IsOnlyTypeFamily(mergedTypes, "object"))
        {
            return TryMergeObjectSchemas(left, right, mergedTypes, out mergedSchema);
        }

        if (IsOnlyTypeFamily(mergedTypes, "array"))
        {
            return TryMergeArraySchemas(left, right, mergedTypes, out mergedSchema);
        }

        mergedSchema = CreateScalarSchema(mergedTypes);
        return true;
    }

    public static bool IsOpaqueSchema(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object)
            return false;

        if (PlanStepOutputContractResolver.TryGetSchemaTypes(schema, out _))
            return false;

        return !schema.EnumerateObject().Any();
    }

    private static bool TryMergeObjectSchemas(
        JsonElement left,
        JsonElement right,
        IReadOnlyList<string> mergedTypes,
        out JsonElement mergedSchema)
    {
        mergedSchema = default;
        var mergedProperties = new JsonObject();
        var leftProperties = GetProperties(left);
        var rightProperties = GetProperties(right);

        foreach (var propertyName in leftProperties.Keys
                     .Concat(rightProperties.Keys)
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(static value => value, StringComparer.OrdinalIgnoreCase))
        {
            var hasLeft = leftProperties.TryGetValue(propertyName, out var leftProperty);
            var hasRight = rightProperties.TryGetValue(propertyName, out var rightProperty);

            if (hasLeft && hasRight)
            {
                if (!TryMerge(leftProperty, rightProperty, out var mergedProperty))
                    return false;

                mergedProperties[propertyName] = JsonNode.Parse(mergedProperty.GetRawText());
                continue;
            }

            mergedProperties[propertyName] = JsonNode.Parse((hasLeft ? leftProperty : rightProperty).GetRawText());
        }

        var mergedRequired = GetRequired(left)
            .Concat(GetRequired(right))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static value => value, StringComparer.Ordinal)
            .ToArray();

        mergedSchema = JsonSerializer.SerializeToElement(new JsonObject
        {
            ["type"] = CreateTypeNode(mergedTypes),
            ["properties"] = mergedProperties,
            ["required"] = new JsonArray(mergedRequired.Select(static value => JsonValue.Create(value)!).ToArray())
        });
        return true;
    }

    private static bool TryMergeArraySchemas(
        JsonElement left,
        JsonElement right,
        IReadOnlyList<string> mergedTypes,
        out JsonElement mergedSchema)
    {
        mergedSchema = default;
        var leftHasItems = PlanStepOutputContractResolver.TryGetArrayItemSchema(left, out var leftItems);
        var rightHasItems = PlanStepOutputContractResolver.TryGetArrayItemSchema(right, out var rightItems);

        JsonElement mergedItems;
        if (leftHasItems && rightHasItems)
        {
            if (!TryMerge(leftItems, rightItems, out mergedItems))
                return false;
        }
        else if (leftHasItems)
        {
            mergedItems = leftItems.Clone();
        }
        else if (rightHasItems)
        {
            mergedItems = rightItems.Clone();
        }
        else
        {
            mergedItems = PlanStepOutputContractResolver.CreateOpaqueSchema();
        }

        mergedSchema = JsonSerializer.SerializeToElement(new JsonObject
        {
            ["type"] = CreateTypeNode(mergedTypes),
            ["items"] = JsonNode.Parse(mergedItems.GetRawText())
        });
        return true;
    }

    private static JsonElement CreateScalarSchema(IReadOnlyList<string> mergedTypes) =>
        JsonSerializer.SerializeToElement(new JsonObject
        {
            ["type"] = CreateTypeNode(mergedTypes)
        });

    private static JsonNode CreateTypeNode(IReadOnlyList<string> mergedTypes)
    {
        var orderedTypes = mergedTypes
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static value => value, StringComparer.Ordinal)
            .ToArray();

        return orderedTypes.Length == 1
            ? JsonValue.Create(orderedTypes[0])!
            : new JsonArray(orderedTypes.Select(static value => JsonValue.Create(value)!).ToArray());
    }

    private static IReadOnlyDictionary<string, JsonElement> GetProperties(JsonElement schema)
    {
        if (!schema.TryGetProperty("properties", out var properties)
            || properties.ValueKind != JsonValueKind.Object)
        {
            return new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        }

        return properties.EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value.Clone(), StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<string> GetRequired(JsonElement schema)
    {
        if (!schema.TryGetProperty("required", out var required)
            || required.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        return required.EnumerateArray()
            .Where(static item => item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
            .Select(static item => item.GetString()!)
            .ToArray();
    }

    private static IReadOnlyList<string> IntersectTypes(
        IReadOnlyList<string> leftTypes,
        IReadOnlyList<string> rightTypes)
    {
        var intersection = new List<string>();

        foreach (var type in leftTypes)
        {
            if (string.Equals(type, "null", StringComparison.Ordinal))
            {
                if (rightTypes.Contains("null", StringComparer.Ordinal))
                    intersection.Add("null");

                continue;
            }

            if (rightTypes.Contains(type, StringComparer.Ordinal))
            {
                intersection.Add(type);
                continue;
            }

            if (string.Equals(type, "number", StringComparison.Ordinal)
                && rightTypes.Contains("integer", StringComparer.Ordinal))
            {
                intersection.Add("integer");
                continue;
            }

            if (string.Equals(type, "integer", StringComparison.Ordinal)
                && rightTypes.Contains("number", StringComparer.Ordinal))
            {
                intersection.Add("integer");
            }
        }

        return intersection
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static value => value, StringComparer.Ordinal)
            .ToArray();
    }

    private static bool IsOnlyTypeFamily(IReadOnlyList<string> mergedTypes, string baseType)
    {
        var nonNullTypes = mergedTypes
            .Where(type => !string.Equals(type, "null", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return nonNullTypes.Length == 1
               && string.Equals(nonNullTypes[0], baseType, StringComparison.Ordinal);
    }
}

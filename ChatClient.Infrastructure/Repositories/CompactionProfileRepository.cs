using ChatClient.Application.Repositories;
using ChatClient.Domain.Models;
using ChatClient.Infrastructure.Constants;
using ChatClient.Infrastructure.Helpers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChatClient.Infrastructure.Repositories;

public sealed class CompactionProfileRepository : ICompactionProfileRepository
{
    private readonly JsonFileRepository<List<CompactionProfile>> _repository;
    private readonly CompactionStageJsonConverter _stageConverter = new();
    private readonly CompactionProfileJsonConverter _profileConverter = new();

    public CompactionProfileRepository(IConfiguration configuration, ILogger<CompactionProfileRepository> logger)
    {
        var path = StoragePathResolver.ResolveUserPath(
            configuration,
            configuration["CompactionProfiles:FilePath"],
            FilePathConstants.DefaultCompactionProfilesFile);
        var options = new JsonSerializerOptions { WriteIndented = true };
        options.Converters.Add(_stageConverter);
        options.Converters.Add(_profileConverter);
        _repository = new JsonFileRepository<List<CompactionProfile>>(path, logger, options);
    }

    public async Task<IReadOnlyCollection<CompactionProfile>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var profiles = await _repository.ReadAsync(cancellationToken) ?? [];
        if (_stageConverter.ReadAndResetLegacyMigrationFlag() || _profileConverter.ReadAndResetLegacyMigrationFlag() || NormalizeStageIds(profiles))
            await _repository.WriteAsync(profiles, cancellationToken);
        return profiles;
    }

    public Task SaveAllAsync(List<CompactionProfile> profiles, CancellationToken cancellationToken = default)
    {
        NormalizeStageIds(profiles);
        return _repository.WriteAsync(profiles, cancellationToken);
    }

    private static bool NormalizeStageIds(IEnumerable<CompactionProfile> profiles)
    {
        var changed = false;
        foreach (var profile in profiles)
        {
            var usedIds = new HashSet<Guid>();
            foreach (var stage in profile.Stages ?? [])
            {
                if (stage.Id != Guid.Empty && usedIds.Add(stage.Id))
                    continue;

                do
                    stage.Id = Guid.NewGuid();
                while (!usedIds.Add(stage.Id));
                changed = true;
            }
        }
        return changed;
    }

    private sealed class CompactionStageJsonConverter : JsonConverter<CompactionStage>
    {
        private bool migratedLegacyValues;

        public bool ReadAndResetLegacyMigrationFlag()
        {
            var value = migratedLegacyValues;
            migratedLegacyValues = false;
            return value;
        }

        public override CompactionStage Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var document = JsonDocument.ParseValue(ref reader);
            var root = document.RootElement;
            var stage = new CompactionStage
            {
                Id = ReadGuid(root, "Id") ?? Guid.Empty,
                Kind = ReadString(root, "Kind") ?? string.Empty,
                SummaryInstructions = ReadString(root, "SummaryInstructions"),
                SummarizerLlmId = ReadGuid(root, "SummarizerLlmId"),
                SummarizerModelName = ReadString(root, "SummarizerModelName")
            };

            if (ReadInt(root, "RetainedCount") is int retainedCount)
            {
                migratedLegacyValues = true;
                if (stage.Kind == CompactionStageKinds.SlidingWindow)
                    stage.MinimumPreservedTurns = retainedCount;
                else
                    stage.MinimumPreservedGroups = retainedCount;
            }
            else
            {
                stage.MinimumPreservedGroups = ReadInt(root, "MinimumPreservedGroups") ?? 0;
                stage.MinimumPreservedTurns = ReadInt(root, "MinimumPreservedTurns") ?? 0;
            }

            var trigger = ReadLimit(root, "Trigger");
            var target = ReadLimit(root, "Target");
            if (trigger is null || target is null)
            {
                var legacyTrigger = ReadInt(root, "TriggerTokenCount");
                var legacyTarget = ReadInt(root, "TargetTokenCount");
                if (legacyTrigger is not null || legacyTarget is not null)
                {
                    migratedLegacyValues = true;
                    trigger = new CompactionLimit { Kind = CompactionLimitKinds.Tokens, Value = legacyTrigger ?? 0 };
                    target = new CompactionLimit { Kind = CompactionLimitKinds.Tokens, Value = legacyTarget ?? 0 };
                }
            }

            stage.Trigger = MigrateInputBudgetPercent(trigger ?? new CompactionLimit());
            stage.Target = MigrateInputBudgetPercent(target ?? new CompactionLimit());
            return stage;
        }

        public override void Write(Utf8JsonWriter writer, CompactionStage value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteString("Id", value.Id);
            writer.WriteString("Kind", value.Kind);
            writer.WritePropertyName("Trigger");
            JsonSerializer.Serialize(writer, value.Trigger, options);
            writer.WritePropertyName("Target");
            JsonSerializer.Serialize(writer, value.Target, options);
            writer.WriteNumber("MinimumPreservedGroups", value.MinimumPreservedGroups);
            writer.WriteNumber("MinimumPreservedTurns", value.MinimumPreservedTurns);
            if (value.SummaryInstructions is not null)
                writer.WriteString("SummaryInstructions", value.SummaryInstructions);
            if (value.SummarizerLlmId is Guid serverId)
                writer.WriteString("SummarizerLlmId", serverId);
            if (value.SummarizerModelName is not null)
                writer.WriteString("SummarizerModelName", value.SummarizerModelName);
            writer.WriteEndObject();
        }

        private static CompactionLimit? ReadLimit(JsonElement root, string name)
        {
            if (!TryGet(root, name, out var element) || element.ValueKind != JsonValueKind.Object)
                return null;
            return new CompactionLimit { Kind = ReadString(element, "Kind") ?? string.Empty, Value = ReadDouble(element, "Value") ?? 0 };
        }

        private CompactionLimit MigrateInputBudgetPercent(CompactionLimit limit)
        {
            if (limit.Kind != "input-budget-percentage")
                return limit;

            migratedLegacyValues = true;
            limit.Kind = CompactionLimitKinds.InputBudgetPercent;
            limit.Value /= 100;
            return limit;
        }

        private static string? ReadString(JsonElement root, string name) =>
            TryGet(root, name, out var element) && element.ValueKind == JsonValueKind.String ? element.GetString() : null;

        private static int? ReadInt(JsonElement root, string name) =>
            TryGet(root, name, out var element) && element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var value) ? value : null;

        private static double? ReadDouble(JsonElement root, string name) =>
            TryGet(root, name, out var element) && element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var value) ? value : null;

        private static Guid? ReadGuid(JsonElement root, string name) =>
            TryGet(root, name, out var element) && element.ValueKind == JsonValueKind.String && element.TryGetGuid(out var value) ? value : null;

        private static bool TryGet(JsonElement root, string name, out JsonElement result)
        {
            foreach (var property in root.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                { result = property.Value; return true; }
            }
            result = default;
            return false;
        }
    }

    private sealed class CompactionProfileJsonConverter : JsonConverter<CompactionProfile>
    {
        private bool migratedLegacyValues;

        public bool ReadAndResetLegacyMigrationFlag()
        {
            var value = migratedLegacyValues;
            migratedLegacyValues = false;
            return value;
        }

        public override CompactionProfile Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var document = JsonDocument.ParseValue(ref reader);
            var root = document.RootElement;
            var toolResultThreshold = ReadDouble(root, "ToolResultThreshold");
            var truncationThreshold = ReadDouble(root, "TruncationThreshold");
            if (toolResultThreshold is null || truncationThreshold is null)
            {
                migratedLegacyValues = true;
                toolResultThreshold ??= (ReadDouble(root, "ToolResultThresholdPercentage") ?? 50) / 100;
                truncationThreshold ??= (ReadDouble(root, "TruncationThresholdPercentage") ?? 80) / 100;
            }

            return new CompactionProfile
            {
                Id = ReadGuid(root, "Id") ?? Guid.NewGuid(),
                Name = ReadString(root, "Name") ?? string.Empty,
                Kind = ReadString(root, "Kind") ?? CompactionProfileKinds.ContextWindow,
                BudgetSource = ReadString(root, "BudgetSource") ?? CompactionBudgetSources.SelectedModel,
                ContextWindowTokens = ReadInt(root, "ContextWindowTokens"),
                MaxOutputTokens = ReadInt(root, "MaxOutputTokens"),
                ToolResultThreshold = toolResultThreshold.Value,
                TruncationThreshold = truncationThreshold.Value,
                CreatedAt = ReadDateTime(root, "CreatedAt") ?? DateTime.UtcNow,
                UpdatedAt = ReadDateTime(root, "UpdatedAt") ?? DateTime.UtcNow,
                Stages = ReadStages(root, options)
            };
        }

        public override void Write(Utf8JsonWriter writer, CompactionProfile value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteString("Id", value.Id);
            writer.WriteString("Name", value.Name);
            writer.WriteString("Kind", value.Kind);
            writer.WriteString("BudgetSource", value.BudgetSource);
            if (value.ContextWindowTokens is int contextWindowTokens)
                writer.WriteNumber("ContextWindowTokens", contextWindowTokens);
            if (value.MaxOutputTokens is int maxOutputTokens)
                writer.WriteNumber("MaxOutputTokens", maxOutputTokens);
            writer.WriteNumber("ToolResultThreshold", value.ToolResultThreshold);
            writer.WriteNumber("TruncationThreshold", value.TruncationThreshold);
            writer.WritePropertyName("Stages");
            JsonSerializer.Serialize(writer, value.Stages, options);
            writer.WriteString("CreatedAt", value.CreatedAt);
            writer.WriteString("UpdatedAt", value.UpdatedAt);
            writer.WriteEndObject();
        }

        private static List<CompactionStage> ReadStages(JsonElement root, JsonSerializerOptions options) =>
            TryGet(root, "Stages", out var stages) && stages.ValueKind == JsonValueKind.Array
                ? JsonSerializer.Deserialize<List<CompactionStage>>(stages.GetRawText(), options) ?? []
                : [];

        private static string? ReadString(JsonElement root, string name) =>
            TryGet(root, name, out var element) && element.ValueKind == JsonValueKind.String ? element.GetString() : null;

        private static int? ReadInt(JsonElement root, string name) =>
            TryGet(root, name, out var element) && element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var value) ? value : null;

        private static double? ReadDouble(JsonElement root, string name) =>
            TryGet(root, name, out var element) && element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var value) ? value : null;

        private static Guid? ReadGuid(JsonElement root, string name) =>
            TryGet(root, name, out var element) && element.ValueKind == JsonValueKind.String && element.TryGetGuid(out var value) ? value : null;

        private static DateTime? ReadDateTime(JsonElement root, string name) =>
            TryGet(root, name, out var element) && element.ValueKind == JsonValueKind.String && element.TryGetDateTime(out var value) ? value : null;

        private static bool TryGet(JsonElement root, string name, out JsonElement result)
        {
            foreach (var property in root.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                { result = property.Value; return true; }
            }
            result = default;
            return false;
        }
    }
}

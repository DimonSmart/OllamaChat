using ChatClient.Shared.Models;
using Microsoft.Extensions.Logging;

namespace ChatClient.Shared.Helpers;

/// <summary>
/// Helper for determining effective server+model pair with selection logging
/// </summary>
public static class ModelSelectionHelper
{
    /// <summary>
    /// Combines configuration and UI selections into a required model. Throws if incomplete.
    /// </summary>
    /// <param name="configuredModel">Model specified in configuration (agent, settings, etc.)</param>
    /// <param name="uiSelectedModel">Model selected in UI</param>
    /// <param name="context">Context description for logging (e.g., "Agent: Kant", "Embedding", "Default chat")</param>
    /// <param name="logger">Logger for recording selection</param>
    /// <returns>Effective server+model pair</returns>
    public static ServerModel GetEffectiveModel(
        ServerModelSelection configuredModel,
        ServerModelSelection uiSelectedModel,
        string context,
        ILogger? logger = null)
    {
        if (!TryGetEffectiveModel(configuredModel, uiSelectedModel, out var effectiveModel))
            throw new InvalidOperationException($"Model selection for {context} is incomplete.");

        var serverFrom = uiSelectedModel.ServerId.HasValue ? "UI selection" : "configuration";
        var modelFrom = !string.IsNullOrWhiteSpace(uiSelectedModel.ModelName) ? "UI selection" : "configuration";
        var source = serverFrom == modelFrom ? serverFrom : "combined";

        logger?.LogDebug(
            "Model selection for {Context}: using {ModelName} on {ServerName} from {Source}",
            context,
            effectiveModel.ModelName,
            GetServerDisplayName(effectiveModel.ServerId),
            source);

        return effectiveModel;
    }

    public static bool TryGetEffectiveModel(
        ServerModelSelection configuredModel,
        ServerModelSelection uiSelectedModel,
        out ServerModel effectiveModel)
    {
        var serverId = uiSelectedModel.ServerId ?? configuredModel.ServerId;
        var modelName = uiSelectedModel.ModelName ?? configuredModel.ModelName;

        if (serverId is { } id && id != Guid.Empty && !string.IsNullOrWhiteSpace(modelName))
        {
            effectiveModel = new(id, modelName);
            return true;
        }

        effectiveModel = default!;
        return false;
    }

    /// <summary>
    /// Determines effective embedding model based on special configuration or fallback to regular model
    /// </summary>
    /// <param name="embeddingModel">Special embedding model from settings</param>
    /// <param name="defaultModel">Default model</param>
    /// <param name="context">Context description for logging</param>
    /// <param name="logger">Logger for recording selection</param>
    /// <returns>Effective embedding model</returns>
    public static ServerModel GetEffectiveEmbeddingModel(
        ServerModelSelection embeddingModel,
        ServerModelSelection defaultModel,
        string context,
        ILogger? logger = null)
    {
        var isEmbeddingModelValid = IsValidModel(embeddingModel);

        ServerModel effectiveModel;
        string source;

        if (isEmbeddingModelValid)
        {
            effectiveModel = new(embeddingModel.ServerId!.Value, embeddingModel.ModelName!);
            source = "embedding configuration";
        }
        else
        {
            effectiveModel = new(defaultModel.ServerId!.Value, defaultModel.ModelName!);
            source = "default model fallback";
        }

        logger?.LogDebug("Embedding model selection for {Context}: using {ModelName} on {ServerName} from {Source}",
            context,
            effectiveModel.ModelName,
            GetServerDisplayName(effectiveModel.ServerId),
            source);

        return effectiveModel;
    }



    /// <summary>
    /// Validates model by checking if both server and model name are specified
    /// </summary>
    private static bool IsValidModel(ServerModelSelection model)
    {
        return model.ServerId is { } id && id != Guid.Empty &&
               !string.IsNullOrWhiteSpace(model.ModelName);
    }

    private static string GetServerDisplayName(Guid serverId)
    {
        return serverId == Guid.Empty ? "Unknown" : serverId.ToString()[..8];
    }
}

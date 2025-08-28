using ChatClient.Shared.Models;
using Microsoft.Extensions.Logging;

namespace ChatClient.Shared.Helpers;

/// <summary>
/// Помогает определить эффективную пару сервер+модель с логированием выбора
/// </summary>
public static class ModelSelectionHelper
{
    /// <summary>
    /// Определяет эффективную пару сервер+модель на основе приоритета: конфигурация > UI выбор
    /// </summary>
    /// <param name="configuredModel">Модель, заданная в конфигурации (агент, настройки и т.д.)</param>
    /// <param name="uiSelectedModel">Модель, выбранная в UI</param>
    /// <param name="context">Описание контекста для логирования (например, "Agent: Kant", "Embedding", "Default chat")</param>
    /// <param name="logger">Logger для записи выбора</param>
    /// <returns>Эффективная пара сервер+модель</returns>
    public static ServerModel GetEffectiveModel(
        ServerModel? configuredModel,
        ServerModel uiSelectedModel,
        string context,
        ILogger? logger = null)
    {
        var isConfiguredModelValid = IsValidModel(configuredModel);

        ServerModel effectiveModel;
        string source;

        if (isConfiguredModelValid)
        {
            effectiveModel = configuredModel!;
            source = "configuration";
        }
        else
        {
            effectiveModel = uiSelectedModel;
            source = "UI selection";
        }

        logger?.LogDebug("Model selection for {Context}: using {ModelName} on {ServerName} from {Source}",
            context,
            effectiveModel.ModelName,
            GetServerDisplayName(effectiveModel.ServerId),
            source);

        return effectiveModel;
    }

    /// <summary>
    /// Определяет эффективную модель для встраивания (embedding) на основе специальной настройки или fallback на обычную модель
    /// </summary>
    /// <param name="embeddingModel">Специальная модель для embedding из настроек</param>
    /// <param name="defaultModel">Модель по умолчанию</param>
    /// <param name="context">Описание контекста для логирования</param>
    /// <param name="logger">Logger для записи выбора</param>
    /// <returns>Эффективная модель для embedding</returns>
    public static ServerModel GetEffectiveEmbeddingModel(
        ServerModel embeddingModel,
        ServerModel defaultModel,
        string context,
        ILogger? logger = null)
    {
        var isEmbeddingModelValid = IsValidModel(embeddingModel);

        ServerModel effectiveModel;
        string source;

        if (isEmbeddingModelValid)
        {
            effectiveModel = embeddingModel;
            source = "embedding configuration";
        }
        else
        {
            effectiveModel = defaultModel;
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
    /// Определяет эффективную модель для агента на основе его настроек или UI выбора
    /// </summary>
    /// <param name="agent">Агент с возможно настроенными LlmId и ModelName</param>
    /// <param name="uiSelectedModel">Модель, выбранная в UI</param>
    /// <param name="logger">Logger для записи выбора</param>
    /// <returns>Эффективная модель для агента</returns>
    public static ServerModel GetEffectiveAgentModel(
        AgentDescription agent,
        ServerModel uiSelectedModel,
        ILogger? logger = null)
    {
        var configuredModel = agent.LlmId.HasValue && agent.LlmId != Guid.Empty && !string.IsNullOrWhiteSpace(agent.ModelName)
            ? new ServerModel(agent.LlmId.Value, agent.ModelName)
            : null;

        return GetEffectiveModel(
            configuredModel,
            uiSelectedModel,
            $"Agent: {agent.AgentName}",
            logger);
    }

    /// <summary>
    /// Проверяет, является ли модель валидной (указаны и сервер и название модели)
    /// </summary>
    private static bool IsValidModel(ServerModel? model)
    {
        return model != null &&
               model.ServerId != Guid.Empty &&
               !string.IsNullOrWhiteSpace(model.ModelName);
    }

    /// <summary>
    /// Получает отображаемое имя сервера для логирования
    /// </summary>
    private static string GetServerDisplayName(Guid serverId)
    {
        return serverId == Guid.Empty ? "Unknown" : serverId.ToString()[..8];
    }
}
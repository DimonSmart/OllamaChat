using ChatClient.Shared.Models;
using ChatClient.Shared.Services;

namespace ChatClient.Api.Services;

public static class LlmServerConfigHelper
{
    /// <summary>
    /// Получает конфигурацию сервера по ID с возможностью fallback на сервер по умолчанию
    /// </summary>
    /// <param name="userSettingsService">Сервис настроек пользователя</param>
    /// <param name="serverId">ID сервера (может быть null или Guid.Empty)</param>
    /// <param name="serverType">Тип сервера для фильтрации (опционально)</param>
    /// <returns>Конфигурация сервера или null если не найдена</returns>
    public static async Task<LlmServerConfig?> GetServerConfigAsync(
        IUserSettingsService userSettingsService,
        Guid? serverId = null,
        ServerType? serverType = null)
    {
        var settings = await userSettingsService.GetSettingsAsync();
        return GetServerConfig(settings, serverId, serverType);
    }

    /// <summary>
    /// Получает конфигурацию сервера по ID с возможностью fallback на сервер по умолчанию
    /// </summary>
    /// <param name="settings">Настройки пользователя</param>
    /// <param name="serverId">ID сервера (может быть null или Guid.Empty)</param>
    /// <param name="serverType">Тип сервера для фильтрации (опционально)</param>
    /// <returns>Конфигурация сервера или null если не найдена</returns>
    public static LlmServerConfig? GetServerConfig(
        UserSettings settings,
        Guid? serverId = null,
        ServerType? serverType = null)
    {
        var servers = serverType.HasValue
            ? settings.Llms.Where(s => s.ServerType == serverType.Value)
            : settings.Llms;

        LlmServerConfig? server = null;

        // Сначала пытаемся найти сервер по заданному ID
        if (serverId.HasValue && serverId.Value != Guid.Empty)
        {
            server = servers.FirstOrDefault(s => s.Id == serverId.Value);
        }

        // Если не найден, пытаемся использовать сервер по умолчанию
        if (server == null && settings.DefaultLlmId.HasValue)
        {
            server = servers.FirstOrDefault(s => s.Id == settings.DefaultLlmId.Value);
        }

        // Если и его нет, берем первый доступный сервер нужного типа
        server ??= servers.FirstOrDefault();

        return server;
    }
}
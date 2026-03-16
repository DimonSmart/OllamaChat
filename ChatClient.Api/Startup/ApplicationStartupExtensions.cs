using ChatClient.Api.Services;
using ChatClient.Api.Services.Seed;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Logging;

namespace ChatClient.Api.Startup;

internal static class ApplicationStartupExtensions
{
    public static async Task InitializeApplicationAsync(this WebApplication app)
    {
        using var scope = app.Services.CreateScope();

        await scope.ServiceProvider.GetRequiredService<AgentDescriptionSeeder>().SeedAsync();
        await scope.ServiceProvider.GetRequiredService<LlmServerConfigSeeder>().SeedAsync();
        await scope.ServiceProvider.GetRequiredService<McpServerConfigSeeder>().SeedAsync();
        await scope.ServiceProvider.GetRequiredService<RagFilesSeeder>().SeedAsync();

        var startupChecker = scope.ServiceProvider.GetRequiredService<OllamaServerAvailabilityService>();
        var ollamaStatus = await startupChecker.CheckOllamaStatusAsync();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();

        if (ollamaStatus.IsAvailable)
        {
            await scope.ServiceProvider.GetRequiredService<McpFunctionIndexService>().BuildIndexAsync();
            logger.LogInformation("Ollama is available and ready.");
            return;
        }

        logger.LogWarning("Ollama is not available - {Error}", ollamaStatus.ErrorMessage);
        logger.LogWarning("Ollama features are limited. Open '/llm-servers' to configure server access.");
    }

    public static void RegisterBrowserLaunch(this WebApplication app)
    {
        app.Lifetime.ApplicationStarted.Register(() =>
        {
            try
            {
                var logger = app.Services.GetRequiredService<ILogger<Program>>();
                var server = app.Services.GetRequiredService<IServer>();
                var addressesFeature = server.Features.Get<IServerAddressesFeature>();

                if (addressesFeature is null || !addressesFeature.Addresses.Any())
                {
                    logger.LogWarning("No server addresses were found. Browser cannot be launched.");
                    return;
                }

                var httpAddress = addressesFeature.Addresses.FirstOrDefault(static address => address.StartsWith("http://", StringComparison.Ordinal));
                var httpsAddress = addressesFeature.Addresses.FirstOrDefault(static address => address.StartsWith("https://", StringComparison.Ordinal));
                var launchUrl = httpsAddress ?? httpAddress;

                if (!string.IsNullOrWhiteSpace(launchUrl))
                    BrowserLaunchService.DisplayInfoAndLaunchBrowser(launchUrl, httpsAddress ?? "N/A");
            }
            catch (Exception ex)
            {
                app.Services
                    .GetRequiredService<ILogger<Program>>()
                    .LogError(ex, "Error during application startup");
            }
        });
    }
}

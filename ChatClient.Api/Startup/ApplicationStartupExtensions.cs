using ChatClient.Api.Services;
using ChatClient.Api.Services.Rag;
using ChatClient.Api.Services.Seed;
using ChatClient.Application.Repositories;
using ChatClient.Domain.Models;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Logging;

namespace ChatClient.Api.Startup;

internal static class ApplicationStartupExtensions
{
    private static readonly Guid DemoTodoProviderProfileId = Guid.Parse("9bbf0bcb-d651-466c-87f7-c4d949bc2a3c");
    private const string DemoTodoProviderProfileName = "MAF Planning Assistant (Demo)";
    private static readonly Guid DemoAgentModeProviderProfileId = Guid.Parse("bcd47fb2-74df-4a5d-9767-2dd0ed2b8aa3");
    private const string DemoAgentModeProviderProfileName = "MAF Plan / Execute (Demo)";

    public static async Task InitializeApplicationAsync(this WebApplication app)
    {
        using var scope = app.Services.CreateScope();

        await scope.ServiceProvider.GetRequiredService<AgentTemplateSeeder>().SeedAsync();
        await SeedTodoProviderProfilesAsync(scope.ServiceProvider);
        await SeedAgentModeProviderProfilesAsync(scope.ServiceProvider);
        await scope.ServiceProvider.GetRequiredService<WorkflowDefinitionSeeder>().SeedAsync();
        await scope.ServiceProvider.GetRequiredService<LlmServerConfigSeeder>().SeedAsync();
        await scope.ServiceProvider.GetRequiredService<McpServerConfigSeeder>().SeedAsync();
        await scope.ServiceProvider.GetRequiredService<LegacyRagMigrationService>().MigrateAsync();

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

    private static async Task SeedTodoProviderProfilesAsync(IServiceProvider services)
    {
        var repository = services.GetRequiredService<ITodoProviderProfileRepository>();
        var profiles = (await repository.GetAllAsync()).ToList();

        if (profiles.Any(profile =>
                profile.Id == DemoTodoProviderProfileId ||
                string.Equals(profile.Name, DemoTodoProviderProfileName, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var now = DateTime.UtcNow;
        profiles.Add(new TodoProviderProfile
        {
            Id = DemoTodoProviderProfileId,
            Name = DemoTodoProviderProfileName,
            Instructions = "You are a helpful planning assistant. Use your todo list to plan and track multi-step work.",
            SuppressTodoListMessage = false,
            TodoListMessageTemplate = null,
            CreatedAt = now,
            UpdatedAt = now
        });

        await repository.SaveAllAsync(profiles);
    }

    private static async Task SeedAgentModeProviderProfilesAsync(IServiceProvider services)
    {
        var repository = services.GetRequiredService<IAgentModeProviderProfileRepository>();
        var profiles = (await repository.GetAllAsync()).ToList();

        if (profiles.Any(profile =>
                profile.Id == DemoAgentModeProviderProfileId ||
                string.Equals(profile.Name, DemoAgentModeProviderProfileName, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var now = DateTime.UtcNow;
        profiles.Add(new AgentModeProviderProfile
        {
            Id = DemoAgentModeProviderProfileId,
            Name = DemoAgentModeProviderProfileName,
            Instructions = null,
            DefaultMode = "plan",
            Modes =
            [
                new AgentModeProfile
                {
                    Name = "plan",
                    Instructions =
                        """
                        Use this mode when analyzing requirements, breaking down tasks, and creating plans. This is the interactive mode — ask clarifying questions, discuss options, and get user approval before proceeding.

                        Process to follow when in plan mode:
                        1. Analyze the request with the purpose of building a research plan.
                        2. Create a list of todo items.
                        3. If needed, use the provided tools to do some exploratory checks to help build a plan and determine what clarifying questions you may need from the user.
                        4. Ask for clarifications from the user where needed.
                          1. Ask each clarification one by one.
                          2. When asking for clarification and you have specific options in mind, present them to the user, so they can choose the option instead of having to retype the entire response.
                          3. Do not proceed until you have received all the needed clarifications.
                          4. Do short exploratory research if it helps with being able to ask sensible clarifications from the user.
                        5. Write the plan to a memory file, so that it is retained even if compaction happens. Make sure to update the plan file if the user requests changes.
                        6. Present the plan to the user and ask for approval to switch to execute mode and process the plan.
                        7. When approval is granted, always switch to execute mode (using the `mode_set` tool), and follow the steps for *Execute mode*.
                        """
                },
                new AgentModeProfile
                {
                    Name = "execute",
                    Instructions =
                        """
                        Determine the type of ask:
                        1. Simple question that doesn't require any further work to answer.
                        2. Any other work, including complex user request that requires a multi-step process to satisfy.

                        If 1. just answer the question directly.
                        If 2. Work autonomously using your best judgment — do not ask the user questions or wait for feedback and follow the following process:
                        1. If you don't have a plan or tasks yet, analyze the user request and create tasks and a plan. (**Skip this step if you came from plan mode**)
                        2. Work autonomously — use your best judgment to make decisions and keep progressing without asking the user questions. The goal is to have a complete, useful result ready when the user returns.
                        3. If you encounter ambiguity or an unexpected situation during execution, choose the most reasonable option, note your choice, and keep going.
                        4. Mark tasks as completed as you finish them.
                        5. Continue working, thinking and calling tools until you have the research result for the user.
                        """
                }
            ],
            CreatedAt = now,
            UpdatedAt = now
        });

        await repository.SaveAllAsync(profiles);
    }
}

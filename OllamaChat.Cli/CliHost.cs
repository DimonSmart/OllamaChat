using ChatClient.Api;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text;

namespace OllamaChat.Cli;

public static class CliHost
{
    public static async Task<int> RunAsync(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        Console.InputEncoding = new UTF8Encoding(false);

        CliArguments command;
        try
        {
            command = CliCommands.Parse(args, Console.IsInputRedirected);
        }
        catch (CliUsageException ex)
        {
            await Console.Error.WriteLineAsync(ex.Message);
            await Console.Error.WriteLineAsync("Run 'ollamachat --help' for usage.");
            return CliExitCodes.Arguments;
        }

        if (command.Kind == CliCommandKind.Help)
        {
            await Console.Out.WriteLineAsync(CliCommands.HelpText);
            return CliExitCodes.Success;
        }

        var initialWorkingDirectory = Directory.GetCurrentDirectory();
        using var cancellation = new CancellationTokenSource();
        var interrupted = false;
        ConsoleCancelEventHandler onCancel = (_, e) =>
        {
            e.Cancel = true;
            interrupted = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += onCancel;
        try
        {
            // The generic host is built but deliberately never started: no web host,
            // scheduler, indexer or other IHostedService is run.
            var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
            {
                Args = [],
                ContentRootPath = AppContext.BaseDirectory
            });
            // The web host loads the Api project's user secrets; reuse the same
            // identity so cloud providers work without duplicating credentials.
            builder.Configuration.AddUserSecrets(typeof(ServiceCollectionExtensions).Assembly,
                optional: true);
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["McpSampling:Disabled"] = "true"
            });
            try
            {
                var settings = new CliConfigurationStore(builder.Configuration, initialWorkingDirectory);
                if (command.Kind == CliCommandKind.Config)
                {
                    switch (command.ConfigAction)
                    {
                        case "set":
                            settings.Set(command.ConfigKey!, command.ConfigValue!, command.ConfigLocal);
                            break;
                        case "unset":
                            settings.Unset(command.ConfigKey!, command.ConfigLocal);
                            break;
                        case "show":
                            await Console.Out.WriteLineAsync(settings.Show(command.ConfigLocal, command.ConfigEffective));
                            break;
                    }
                    return CliExitCodes.Success;
                }
                if (command.Kind == CliCommandKind.Run)
                {
                    command = settings.Resolve(command);
                    if (command.Agent is not null && command.Prompt is null &&
                        command.PromptFile is null && !command.FromStdin)
                    {
                        await Console.Out.WriteLineAsync(CliCommands.HelpText);
                        return CliExitCodes.Success;
                    }
                    cancellation.CancelAfter(command.Timeout);
                }
            }
            catch (CliUsageException ex)
            {
                await Console.Error.WriteLineAsync(ex.Message);
                return CliExitCodes.Arguments;
            }

            builder.Logging.ClearProviders();
            builder.Logging.AddDebug();
            builder.Services.AddCliAgentServices();
            using var host = builder.Build();
            await using var scope = host.Services.CreateAsyncScope();
            var code = await CliCommands.ExecuteAsync(command, scope.ServiceProvider,
                Console.In, Console.Out, Console.Error, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            return code;
        }
        catch (OperationCanceledException) when (interrupted)
        {
            await Console.Error.WriteLineAsync("Canceled by user.");
            return CliExitCodes.Canceled;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            await Console.Error.WriteLineAsync("Agent execution timed out.");
            return CliExitCodes.Timeout;
        }
        catch (Exception ex)
        {
            // Do not print exception messages: provider exceptions may contain secrets.
            var incident = Guid.NewGuid().ToString("N");
            var path = await TryRecordFailureAsync(incident, ex);
            await Console.Error.WriteLineAsync(
                path is null
                    ? $"Internal CLI error ({incident}). Diagnostic log is unavailable."
                    : $"Internal CLI error ({incident}). See local diagnostics: {path}");
            return CliExitCodes.InternalError;
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
        }
    }

    private static async Task<string?> TryRecordFailureAsync(string incident, Exception error)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "OllamaChat", "logs");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "cli-errors.log");
            // Exception messages and Data are deliberately excluded: they may contain credentials.
            var message = error.Message;
            // These specific DI messages contain service type names, not invocation data.
            // All other provider exception messages remain excluded from diagnostics.
            var safeDiMessage = error is InvalidOperationException &&
                (message.StartsWith("Unable to resolve service for type '", StringComparison.Ordinal) ||
                 message.StartsWith("A circular dependency was detected for the service of type ", StringComparison.Ordinal) ||
                 message.StartsWith("No service for type ", StringComparison.Ordinal))
                ? message
                : string.Empty;
            var details = $"{DateTimeOffset.UtcNow:O} incident={incident} type={error.GetType().FullName}{Environment.NewLine}" +
                          $"{safeDiMessage}{Environment.NewLine}{error.StackTrace}{Environment.NewLine}";
            await File.AppendAllTextAsync(path, details);
            return path;
        }
        catch (Exception)
        {
            return null;
        }
    }
}

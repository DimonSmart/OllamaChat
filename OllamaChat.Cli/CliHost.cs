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
            if (command.Kind == CliCommandKind.Run)
                cancellation.CancelAfter(command.Timeout);

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
        catch (Exception)
        {
            // Runtime exceptions may include credentials in message/stack trace.
            await Console.Error.WriteLineAsync("CLI failed unexpectedly. Check the application diagnostics.");
            return CliExitCodes.InternalError;
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
        }
    }
}

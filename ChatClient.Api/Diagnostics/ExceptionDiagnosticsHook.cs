using Serilog;
using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;

namespace ChatClient.Api.Diagnostics;

internal static class ExceptionDiagnosticsHook
{
    private const string LogFirstChanceEnvVar = "OLLAMACHAT_LOG_FIRST_CHANCE_EXCEPTIONS";
    private const string IncludeCanceledEnvVar = "OLLAMACHAT_INCLUDE_CANCELED_FIRST_CHANCE_EXCEPTIONS";
    private static readonly ConcurrentDictionary<string, int> Counts = new();
    private static int _isRegistered;
    private static int _isSummaryWritten;
    private static string _phase = "startup";

    public static void Register()
    {
        if (!IsEnabled())
        {
            return;
        }

        if (Interlocked.Exchange(ref _isRegistered, 1) != 0)
        {
            return;
        }

        AppDomain.CurrentDomain.FirstChanceException += OnFirstChanceException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        Log.Information(
            "Extended exception diagnostics are enabled via {EnvVar}. IncludeCanceled={IncludeCanceled}",
            LogFirstChanceEnvVar,
            ShouldIncludeCanceledExceptions());
    }

    public static void TrackLifetime(IHostApplicationLifetime lifetime)
    {
        if (!IsEnabled())
        {
            return;
        }

        lifetime.ApplicationStarted.Register(() => SetPhase("running"));
        lifetime.ApplicationStopping.Register(() => SetPhase("stopping"));
        lifetime.ApplicationStopped.Register(() =>
        {
            SetPhase("stopped");
            FlushSummary();
        });
    }

    public static void FlushSummary()
    {
        if (!IsEnabled() || Interlocked.Exchange(ref _isSummaryWritten, 1) != 0)
        {
            return;
        }

        if (Counts.IsEmpty)
        {
            Log.Information("First-chance exception summary: no exceptions were captured.");
            return;
        }

        foreach (var entry in Counts.OrderByDescending(static pair => pair.Value).ThenBy(static pair => pair.Key))
        {
            var parts = entry.Key.Split('|', 3);
            var phase = parts.Length > 0 ? parts[0] : "unknown";
            var exceptionType = parts.Length > 1 ? parts[1] : "unknown";
            var message = parts.Length > 2 ? parts[2] : string.Empty;

            Log.Information(
                "First-chance exception summary. Phase={Phase} ExceptionType={ExceptionType} Count={Count} Message={Message}",
                phase,
                exceptionType,
                entry.Value,
                message);
        }
    }

    private static void OnFirstChanceException(object? sender, FirstChanceExceptionEventArgs args)
    {
        if (!ShouldLogException(args.Exception))
        {
            return;
        }

        var phase = Volatile.Read(ref _phase);
        var exceptionType = args.Exception.GetType().FullName ?? args.Exception.GetType().Name;
        var key = $"{phase}|{exceptionType}|{args.Exception.Message}";
        var count = Counts.AddOrUpdate(key, 1, static (_, current) => current + 1);

        if (count == 1)
        {
            Log.Warning(
                args.Exception,
                "First-chance exception captured. Phase={Phase} ExceptionType={ExceptionType} Source={Source}",
                phase,
                exceptionType,
                args.Exception.Source);
        }
    }

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs args)
    {
        if (args.ExceptionObject is Exception exception)
        {
            Log.Fatal(
                exception,
                "Unhandled exception captured. Phase={Phase} IsTerminating={IsTerminating}",
                Volatile.Read(ref _phase),
                args.IsTerminating);
        }
        else
        {
            Log.Fatal(
                "Unhandled non-Exception object captured. Phase={Phase} IsTerminating={IsTerminating} Value={Value}",
                Volatile.Read(ref _phase),
                args.IsTerminating,
                args.ExceptionObject);
        }
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs args)
    {
        Log.Error(
            args.Exception,
            "Unobserved task exception captured. Phase={Phase}",
            Volatile.Read(ref _phase));
    }

    private static bool IsEnabled()
    {
        var value = Environment.GetEnvironmentVariable(LogFirstChanceEnvVar);
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.Equals("1", StringComparison.OrdinalIgnoreCase)
            || value.Equals("true", StringComparison.OrdinalIgnoreCase)
            || value.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldIncludeCanceledExceptions()
    {
        var value = Environment.GetEnvironmentVariable(IncludeCanceledEnvVar);
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.Equals("1", StringComparison.OrdinalIgnoreCase)
            || value.Equals("true", StringComparison.OrdinalIgnoreCase)
            || value.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldLogException(Exception exception)
    {
        if (ShouldIncludeCanceledExceptions())
        {
            return true;
        }

        return exception is not OperationCanceledException and not TaskCanceledException;
    }

    private static void SetPhase(string phase)
    {
        Interlocked.Exchange(ref _phase, phase);
        Log.Information("Diagnostic exception phase changed to {Phase}.", phase);
    }
}

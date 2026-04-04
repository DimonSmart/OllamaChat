using System.Diagnostics;
using System.Runtime.ExceptionServices;
using Serilog;

namespace ChatClient.Api.Diagnostics;

internal static class SemaphoreFullExceptionDebugHook
{
    private const string BreakOnSemaphoreFullEnvVar = "OLLAMACHAT_BREAK_ON_SEMAPHORE_FULL";
    private static int _isRegistered;

    public static void Register()
    {
        if (!Debugger.IsAttached)
        {
            return;
        }

        if (Interlocked.Exchange(ref _isRegistered, 1) != 0)
        {
            return;
        }

        AppDomain.CurrentDomain.FirstChanceException += OnFirstChanceException;
    }

    private static void OnFirstChanceException(object? sender, FirstChanceExceptionEventArgs args)
    {
        if (args.Exception is not SemaphoreFullException exception)
        {
            return;
        }

        var currentStack = new System.Diagnostics.StackTrace(true).ToString();

        Log.Error(
            exception,
            "First-chance SemaphoreFullException captured. ExceptionStack={ExceptionStack} CurrentStack={CurrentStack}",
            exception.StackTrace,
            currentStack);

        if (ShouldBreakIntoDebugger())
        {
            Debugger.Break();
        }
    }

    private static bool ShouldBreakIntoDebugger()
    {
        var value = Environment.GetEnvironmentVariable(BreakOnSemaphoreFullEnvVar);
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }
}

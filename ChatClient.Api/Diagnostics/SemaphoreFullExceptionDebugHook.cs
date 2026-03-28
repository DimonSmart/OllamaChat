using System.Diagnostics;
using System.Runtime.ExceptionServices;
using Serilog;

namespace ChatClient.Api.Diagnostics;

internal static class SemaphoreFullExceptionDebugHook
{
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

        Debugger.Break();
    }
}

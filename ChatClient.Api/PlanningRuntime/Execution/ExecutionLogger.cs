namespace ChatClient.Api.PlanningRuntime.Execution;

public interface IExecutionLogger
{
    void Log(string message);
}

public sealed class NullExecutionLogger : IExecutionLogger
{
    public static readonly NullExecutionLogger Instance = new();

    private NullExecutionLogger()
    {
    }

    public void Log(string message)
    {
    }
}

public sealed class ActionExecutionLogger(Action<string> writeLine) : IExecutionLogger
{
    public void Log(string message)
    {
        writeLine(message);
    }
}


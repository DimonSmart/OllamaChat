using ChatClient.Domain.Models;

using Microsoft.SemanticKernel;


namespace ChatClient.Api.Client.Services;

internal sealed class FunctionCallRecordingFilter : IFunctionInvocationFilter
{
    private readonly List<FunctionCallRecord> _records = [];

    public IReadOnlyList<FunctionCallRecord> Records => _records;

    public void Clear() => _records.Clear();

    public async Task OnFunctionInvocationAsync(Microsoft.SemanticKernel.FunctionInvocationContext context, Func<Microsoft.SemanticKernel.FunctionInvocationContext, Task> next)
    {
        string request = string.Join(", ", context.Arguments.Select(a => $"{a.Key}: {a.Value}"));
        await next(context);

        string response = context.Result?.GetValue<object>()?.ToString() ?? context.Result?.ToString() ?? string.Empty;
        string server = context.Function.PluginName ?? "McpServer";
        string function = context.Function.Name;
        _records.Add(new FunctionCallRecord(server, function, request, response));
    }
}



using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace ChatClient.Api;

public class ApiExceptionFilter(ILogger<ApiExceptionFilter> logger) : IAsyncExceptionFilter
{
    public Task OnExceptionAsync(ExceptionContext context)
    {
        logger.LogError(context.Exception, "Unhandled exception occurred during request.");

        context.Result = new ObjectResult(new { error = "An internal error occurred." })
        {
            StatusCode = StatusCodes.Status500InternalServerError
        };

        context.ExceptionHandled = true;

        return Task.CompletedTask;
    }
}

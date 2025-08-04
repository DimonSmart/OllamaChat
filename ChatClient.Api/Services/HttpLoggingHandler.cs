using System.Text;

namespace ChatClient.Api.Services;

/// <summary>
/// HTTP message handler that logs request and response bodies
/// </summary>
public class HttpLoggingHandler : DelegatingHandler
{
    private readonly ILogger<HttpLoggingHandler> _logger;

    public HttpLoggingHandler(ILogger<HttpLoggingHandler> logger)
    {
        _logger = logger;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        await LogRequestAsync(request);

        var response = await base.SendAsync(request, cancellationToken);

        await LogResponseAsync(response);

        return response;
    }

    private async Task LogRequestAsync(HttpRequestMessage request)
    {
        if (!_logger.IsEnabled(LogLevel.Debug))
            return;

        var requestInfo = new StringBuilder();
        requestInfo.AppendLine($"HTTP Request: {request.Method} {request.RequestUri}");

        if (request.Headers.Any())
        {
            requestInfo.AppendLine("Headers:");
            foreach (var header in request.Headers)
            {
                requestInfo.AppendLine($"  {header.Key}: {string.Join(", ", header.Value)}");
            }
        }

        if (request.Content != null)
        {
            if (request.Content.Headers.Any())
            {
                requestInfo.AppendLine("Content Headers:");
                foreach (var header in request.Content.Headers)
                {
                    requestInfo.AppendLine($"  {header.Key}: {string.Join(", ", header.Value)}");
                }
            }

            var content = await request.Content.ReadAsStringAsync();
            if (!string.IsNullOrEmpty(content))
            {
                requestInfo.AppendLine("Body:");
                requestInfo.AppendLine(content);
            }
        }

        _logger.LogDebug("{RequestInfo}", requestInfo.ToString());
    }

    private async Task LogResponseAsync(HttpResponseMessage response)
    {
        if (!_logger.IsEnabled(LogLevel.Debug))
            return;

        var responseInfo = new StringBuilder();
        responseInfo.AppendLine($"HTTP Response: {(int)response.StatusCode} {response.StatusCode}");

        if (response.Headers.Any())
        {
            responseInfo.AppendLine("Headers:");
            foreach (var header in response.Headers)
            {
                responseInfo.AppendLine($"  {header.Key}: {string.Join(", ", header.Value)}");
            }
        }

        if (response.Content != null)
        {
            if (response.Content.Headers.Any())
            {
                responseInfo.AppendLine("Content Headers:");
                foreach (var header in response.Content.Headers)
                {
                    responseInfo.AppendLine($"  {header.Key}: {string.Join(", ", header.Value)}");
                }
            }

            var content = await response.Content.ReadAsStringAsync();
            if (!string.IsNullOrEmpty(content))
            {
                responseInfo.AppendLine("Body:");
                responseInfo.AppendLine(content);
            }
        }

        _logger.LogDebug("{ResponseInfo}", responseInfo.ToString());
    }
}

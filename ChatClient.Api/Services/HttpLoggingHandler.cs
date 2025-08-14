using System.Text;
using Microsoft.Extensions.Logging;

namespace ChatClient.Api.Services;

public class HttpLoggingHandler(ILogger<HttpLoggingHandler> logger) : DelegatingHandler
{
    private const int MaxBodyChars = 32_768;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        await LogRequestAsync(request, cancellationToken);

        var response = await base.SendAsync(request, cancellationToken);

        await LogResponseAsync(response, cancellationToken);

        return response;
    }

    private async Task LogRequestAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (!logger.IsEnabled(LogLevel.Information))
            return;

        var sb = new StringBuilder();
        sb.AppendLine($"HTTP Request: {request.Method} {request.RequestUri}");

        if (request.Headers.Any())
        {
            sb.AppendLine("Headers:");
            foreach (var header in request.Headers)
                sb.AppendLine($"  {header.Key}: {string.Join(", ", header.Value)}");
        }

        if (request.Content != null)
        {
            try
            { await request.Content.LoadIntoBufferAsync(); }
            catch { }

            if (request.Content.Headers.Any())
            {
                sb.AppendLine("Content Headers:");
                foreach (var header in request.Content.Headers)
                    sb.AppendLine($"  {header.Key}: {string.Join(", ", header.Value)}");
            }

            var content = await SafeReadAsStringAsync(request.Content, ct);
            if (!string.IsNullOrEmpty(content))
            {
                sb.AppendLine("Body:");
                sb.AppendLine(Truncate(content, MaxBodyChars));
            }
        }

        logger.LogInformation(sb.ToString());
    }

    private async Task LogResponseAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (!logger.IsEnabled(LogLevel.Information))
            return;

        var sb = new StringBuilder();
        sb.AppendLine($"HTTP Response: {(int)response.StatusCode} {response.StatusCode}");

        if (response.Headers.Any())
        {
            sb.AppendLine("Headers:");
            foreach (var header in response.Headers)
                sb.AppendLine($"  {header.Key}: {string.Join(", ", header.Value)}");
        }

        if (response.Content != null)
        {
            if (response.Content.Headers.Any())
            {
                sb.AppendLine("Content Headers:");
                foreach (var header in response.Content.Headers)
                    sb.AppendLine($"  {header.Key}: {string.Join(", ", header.Value)}");
            }

            var content = await SafeReadAsStringAsync(response.Content, ct);
            if (!string.IsNullOrEmpty(content))
            {
                sb.AppendLine("Body:");
                sb.AppendLine(Truncate(content, MaxBodyChars));
            }
        }

        logger.LogInformation(sb.ToString());
    }

    private static async Task<string?> SafeReadAsStringAsync(HttpContent content, CancellationToken ct)
    {
        try
        {
            return await content.ReadAsStringAsync(ct);
        }
        catch
        {
            return "<unreadable body>";
        }
    }

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value.Substring(0, max) + " [truncated]";
}

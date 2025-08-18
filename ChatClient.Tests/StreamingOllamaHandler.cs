using System.Net;
using System.Text;

using Xunit.Abstractions;

#pragma warning disable SKEXP0110
#pragma warning disable SKEXP0001
namespace ChatClient.Tests;

public partial class PhilosopherDebateTests
{
    private sealed class StreamingOllamaHandler(ITestOutputHelper output) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            output.WriteLine($"StreamingOllamaHandler.SendAsync called: {request.Method} {request.RequestUri}");

            // Simulate streaming response with multiple chunks
            string streamingResponse = """
{"model":"qwen3:latest","created_at":"2025-08-18T17:21:01.0384422Z","message":{"role":"assistant","content":"Lying"},"done":false}
{"model":"qwen3:latest","created_at":"2025-08-18T17:21:01.4095154Z","message":{"role":"assistant","content":" is"},"done":false}
{"model":"qwen3:latest","created_at":"2025-08-18T17:21:01.7655802Z","message":{"role":"assistant","content":" a"},"done":false}
{"model":"qwen3:latest","created_at":"2025-08-18T17:21:02.0925197Z","message":{"role":"assistant","content":" complex"},"done":false}
{"model":"qwen3:latest","created_at":"2025-08-18T17:21:02.3664196Z","message":{"role":"assistant","content":" moral"},"done":false}
{"model":"qwen3:latest","created_at":"2025-08-18T17:21:02.6360194Z","message":{"role":"assistant","content":" issue"},"done":false}
{"model":"qwen3:latest","created_at":"2025-08-18T17:21:02.9322536Z","message":{"role":"assistant","content":""},"done_reason":"stop","done":true}
""";

            output.WriteLine($"Returning streaming response with {streamingResponse.Split('\n').Length} chunks");

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(streamingResponse, Encoding.UTF8, "application/x-ndjson")
            });
        }
    }
}

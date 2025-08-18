using System.Net;
using System.Text;
using System.Text.Json;

using Xunit.Abstractions;

#pragma warning disable SKEXP0110
#pragma warning disable SKEXP0001
namespace ChatClient.Tests;

public partial class PhilosopherDebateTests
{
    private sealed class StubOllamaHandler(ITestOutputHelper output) : HttpMessageHandler
    {
        public List<string> ObservedRoles { get; } = [];
        public List<string> ObservedMessages { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            output.WriteLine($"StubOllamaHandler.SendAsync called: {request.Method} {request.RequestUri}");

            if (request.Content == null)
            {
                output.WriteLine("Request content is null");
                return new HttpResponseMessage(HttpStatusCode.BadRequest);
            }

            string payload = await request.Content.ReadAsStringAsync(cancellationToken);
            output.WriteLine($"Request payload: {payload}");

            using JsonDocument doc = JsonDocument.Parse(payload);
            JsonElement messages = doc.RootElement.GetProperty("messages");
            List<JsonElement> messagesArray = messages.EnumerateArray().ToList();

            output.WriteLine($"Found {messagesArray.Count} messages in payload");

            if (messagesArray.Count > 0)
            {
                JsonElement last = messagesArray.Last();
                string role = last.GetProperty("role").GetString()!;
                string content = last.GetProperty("content").GetString() ?? "";

                ObservedRoles.Add(role);
                ObservedMessages.Add(content);

                output.WriteLine($"Last message - Role: {role}, Content: {content}");
            }

            // Simulate streaming response
            const string responseJson = "{\"model\":\"phi4\",\"created_at\":\"2024-01-01T00:00:00Z\",\"message\":{\"role\":\"assistant\",\"content\":\"Philosophical response\"},\"done\":true}";

            output.WriteLine($"Returning response: {responseJson}");

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
            };
        }
    }
}

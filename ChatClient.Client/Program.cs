using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using ChatClient.Client;
using MudBlazor.Services;
using ChatClient.Client.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Logging.SetMinimumLevel(LogLevel.Warning);

var apiUrl = new Uri("http://localhost:5149");
Console.WriteLine($"API URL: {apiUrl}");

builder.Services.AddScoped(sp => {
    var client = new HttpClient { BaseAddress = apiUrl };
    client.DefaultRequestHeaders.Add("X-Client-App", "OllamaChat-Blazor");
    return client;
});

builder.Services.AddScoped<ChatService>();
builder.Services.AddScoped<ClientSystemPromptService>();
builder.Services.AddMudServices();

await builder.Build().RunAsync();

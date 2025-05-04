using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using ChatClient.Client;
using ChatClient.Client.Services;
using ChatClient.Shared.Services;
using MudBlazor;
using MudBlazor.Services;

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
builder.Services.AddScoped<IChatViewModelService, ChatViewModelService>();
builder.Services.AddScoped<ClientSystemPromptService>();
builder.Services.AddScoped<ISystemPromptService, ClientSystemPromptService>();
builder.Services.AddMudServices(config =>
{
    config.SnackbarConfiguration.PositionClass = Defaults.Classes.Position.TopRight;
    config.SnackbarConfiguration.ShowCloseIcon = true;
    config.SnackbarConfiguration.VisibleStateDuration = 5000;
});

await builder.Build().RunAsync();

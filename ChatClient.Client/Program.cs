// This is the entry point for the Blazor WebAssembly client application
// It runs in the browser after the files are delivered by the host API project
// The code here configures the client-side services and bootstraps the Blazor WebAssembly runtime
using ChatClient.Client;
using ChatClient.Client.Services;
using ChatClient.Shared.Services;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using MudBlazor;
using MudBlazor.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Logging.SetMinimumLevel(LogLevel.Warning);

// Configure HttpClient with base address
// Since UI and API are now on the same host, we use the base path
builder.Services.AddScoped(sp =>
{
    var client = new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) };
    client.DefaultRequestHeaders.Add("X-Client-App", "OllamaChat-Blazor");
    client.Timeout = TimeSpan.FromMinutes(10);
    client.DefaultRequestHeaders.Add("Accept", "text/event-stream");
    return client;
});

builder.Services.AddScoped<ChatService>();
builder.Services.AddScoped<IChatService, ChatService>();
builder.Services.AddScoped<IChatViewModelService, ChatViewModelService>();
builder.Services.AddScoped<ClientSystemPromptService>();
builder.Services.AddScoped<ISystemPromptService, ClientSystemPromptService>();
builder.Services.AddScoped<IUserSettingsService, ClientUserSettingsService>();
builder.Services.AddScoped<IModelsService, ModelsService>();
builder.Services.AddScoped<IMcpServerConfigService, ClientMcpServerConfigService>();
builder.Services.AddMudServices(config =>
{
    config.SnackbarConfiguration.PositionClass = Defaults.Classes.Position.TopRight;
    config.SnackbarConfiguration.ShowCloseIcon = true;
    config.SnackbarConfiguration.VisibleStateDuration = 5000;
});

var host = builder.Build();
await host.RunAsync();

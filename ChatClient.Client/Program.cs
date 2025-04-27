using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using ChatClient.Client;
using MudBlazor.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// Включаем подробное логирование
builder.Logging.SetMinimumLevel(LogLevel.Debug);

// Настройка URL API сервера (используем прямой адрес, чтобы избежать проблем с CORS)
var apiUrl = new Uri("http://localhost:5149"); // Стандартный порт в launchSettings.json для API
Console.WriteLine($"API URL: {apiUrl}");

builder.Services.AddScoped(sp => {
    var client = new HttpClient { BaseAddress = apiUrl };
    // Добавим пользовательские заголовки для отладки
    client.DefaultRequestHeaders.Add("X-Client-App", "OllamaChat-Blazor");
    return client;
});

builder.Services.AddMudServices();

await builder.Build().RunAsync();

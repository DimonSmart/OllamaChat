using Microsoft.SemanticKernel;

namespace ChatClient.Api.Services;

public class KernelService
{
    private readonly IConfiguration _configuration;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IHttpClientFactory _httpClientFactory;

    public KernelService(
        IConfiguration configuration,
        ILoggerFactory loggerFactory,
        IHttpClientFactory httpClientFactory)
    {
        _configuration = configuration;
        _loggerFactory = loggerFactory;
        _httpClientFactory = httpClientFactory;
    }

    public Kernel CreateKernel()
    {
        var baseUrl = _configuration["Ollama:BaseUrl"] ?? "http://localhost:11434";
        var modelId = _configuration["Ollama:Model"] ?? "phi4:14b";

        var httpClient = _httpClientFactory.CreateClient("Ollama");
        httpClient.BaseAddress = new Uri(baseUrl);
        httpClient.Timeout = TimeSpan.FromMinutes(10);

        var builder = Kernel.CreateBuilder();
        builder.AddOllamaChatCompletion(
            modelId: modelId,
            httpClient: httpClient,
            serviceId: "ollama"
        );

        builder.Services.AddLogging(c => c.AddConsole().SetMinimumLevel(LogLevel.Information));

        var kernel = builder.Build();

        // Here we will add plugins later
        return kernel;
    }
}

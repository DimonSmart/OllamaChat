using Microsoft.JSInterop;
using System.Text.Json;

namespace ChatClient.Client.Services
{
    public class ApiPortService
    {
        private readonly IJSRuntime _jsRuntime;
        private readonly ILogger<ApiPortService> _logger;
        private const string PORT_STORAGE_KEY = "ollamaChat_apiPort";
        
        public ApiPortService(IJSRuntime jsRuntime, ILogger<ApiPortService> logger)
        {
            _jsRuntime = jsRuntime;
            _logger = logger;
        }
        
        public async Task SaveApiPortAsync(string port)
        {
            if (!string.IsNullOrEmpty(port))
            {
                _logger.LogInformation("Saving API port to localStorage: {Port}", port);
                try
                {
                    await _jsRuntime.InvokeVoidAsync("localStorage.setItem", PORT_STORAGE_KEY, port);
                    _logger.LogInformation("API port saved successfully");
                    
                    // Verify storage
                    var storedPort = await GetSavedApiPortAsync();
                    _logger.LogInformation("Verification - Retrieved port: {StoredPort}", storedPort);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error saving API port to localStorage");
                }
            }
        }
        
        public async Task<string?> GetSavedApiPortAsync()
        {
            try
            {
                _logger.LogInformation("Retrieving API port from localStorage");
                var port = await _jsRuntime.InvokeAsync<string?>("localStorage.getItem", PORT_STORAGE_KEY);
                _logger.LogInformation("Retrieved API port: {Port}", port ?? "(null)");
                return port;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving API port from localStorage");
                return null;
            }
        }
        
        public async Task InitializePortFromQueryStringAsync(Uri baseAddress)
        {
            try
            {
                if (baseAddress == null) return;
                
                var query = System.Web.HttpUtility.ParseQueryString(baseAddress.Query);
                var apiPort = query["apiPort"];
                
                _logger.LogInformation("Extracted API port from query string: {Port}", apiPort ?? "(null)");
                
                if (!string.IsNullOrEmpty(apiPort))
                {
                    await SaveApiPortAsync(apiPort);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error initializing port from query string");
            }
        }
        
        public async Task<Uri> GetBaseAddressWithPortAsync(Uri defaultBaseAddress)
        {
            try
            {
                var savedPort = await GetSavedApiPortAsync();
                
                if (!string.IsNullOrEmpty(savedPort) && int.TryParse(savedPort, out var portValue))
                {
                    var uriBuilder = new UriBuilder(defaultBaseAddress)
                    {
                        Port = portValue
                    };
                    return uriBuilder.Uri;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error building base address with port");
            }
            
            return defaultBaseAddress;
        }
    }
}

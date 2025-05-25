using ChatClient.Shared.Models;
using ChatClient.Shared.Services;
using System.Net.Http.Json;
using System.Text.Json;

namespace ChatClient.Api.Client.Services;

public class ClientMcpServerConfigService(HttpClient httpClient) : IMcpServerConfigService
{
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<List<McpServerConfig>> GetAllServersAsync()
    {
        try
        {
            var response = await httpClient.GetAsync("api/mcpservers");

            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadFromJsonAsync<List<McpServerConfig>>(_jsonOptions) ?? [];
            }

            Console.WriteLine($"Error loading MCP server configs: {response.StatusCode}");
            return [];
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Exception loading MCP server configs: {ex}");
            return [];
        }
    }

    public async Task<McpServerConfig?> GetServerByIdAsync(Guid id)
    {
        try
        {
            var response = await httpClient.GetAsync($"api/mcpservers/{id}");

            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadFromJsonAsync<McpServerConfig>(_jsonOptions);
            }

            return null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error getting MCP server config: {ex}");
            return null;
        }
    }

    public async Task<McpServerConfig> CreateServerAsync(McpServerConfig server)
    {
        try
        {
            var response = await httpClient.PostAsJsonAsync("api/mcpservers", server);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<McpServerConfig>(_jsonOptions) ?? server;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error creating MCP server config: {ex}");
            throw;
        }
    }

    public async Task<McpServerConfig> UpdateServerAsync(McpServerConfig server)
    {
        try
        {
            if (server.Id == null) throw new ArgumentException("Server ID cannot be null or empty when updating");

            var response = await httpClient.PutAsJsonAsync($"api/mcpservers/{server.Id}", server);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<McpServerConfig>(_jsonOptions) ?? server;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error updating MCP server config: {ex}");
            throw;
        }
    }

    public async Task DeleteServerAsync(Guid id)
    {
        var response = await httpClient.DeleteAsync($"api/mcpservers/{id}");
        response.EnsureSuccessStatusCode();
    }
}

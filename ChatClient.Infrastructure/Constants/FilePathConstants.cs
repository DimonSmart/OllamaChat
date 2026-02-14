namespace ChatClient.Infrastructure.Constants;

public static class FilePathConstants
{
    public const string DefaultLlmServersFile = "UserData/llm_servers.json";
    public const string DefaultMcpServersFile = "UserData/mcp_servers.json";
    public const string DefaultUserProfilePrefsFile = "UserData/user_profile.json";
    public const string DefaultAgentDescriptionsFile = "UserData/agent_descriptions.json";
    public const string DefaultUserSettingsFile = "UserData/user_settings.json";
    public const string DefaultSavedChatsDirectory = "UserData/SavedChats";
    public const string DefaultRagFilesDirectory = "UserData/agents";
    public const string DefaultRagVectorDatabaseFile = "UserData/rag/rag.sqlite";
    public const string DefaultSeedDataDirectory = "Data";
}

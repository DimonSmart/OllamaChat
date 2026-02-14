using ChatClient.Infrastructure.Constants;
using Microsoft.Extensions.Configuration;

namespace ChatClient.Infrastructure.Helpers;

public static class StoragePathResolver
{
    private const string StorageRootKey = "Storage:RootPath";
    private const string SeedDataPathKey = "Storage:SeedDataPath";
    private const string StorageRootEnvVar = "OLLAMACHAT_STORAGE_ROOT";

    public static string ResolveUserPath(
        IConfiguration configuration,
        string? configuredPath,
        string fallbackRelativePath)
    {
        var path = string.IsNullOrWhiteSpace(configuredPath)
            ? fallbackRelativePath
            : configuredPath;

        if (Path.IsPathRooted(path))
        {
            return Path.GetFullPath(path);
        }

        var storageRoot = GetStorageRoot(configuration);
        return Path.GetFullPath(Path.Combine(storageRoot, path));
    }

    public static string ResolveSeedPath(
        IConfiguration configuration,
        string contentRootPath,
        string? configuredPath,
        string fallbackRelativePath)
    {
        var relative = string.IsNullOrWhiteSpace(configuredPath)
            ? fallbackRelativePath
            : configuredPath;

        if (Path.IsPathRooted(relative))
        {
            return Path.GetFullPath(relative);
        }

        var seedRootSetting = configuration[SeedDataPathKey];
        var seedRoot = string.IsNullOrWhiteSpace(seedRootSetting)
            ? Path.Combine(contentRootPath, FilePathConstants.DefaultSeedDataDirectory)
            : (Path.IsPathRooted(seedRootSetting)
                ? seedRootSetting
                : Path.Combine(contentRootPath, seedRootSetting));

        return Path.GetFullPath(Path.Combine(seedRoot, relative));
    }

    public static string GetStorageRoot(IConfiguration configuration)
    {
        var configured = configuration[StorageRootKey];
        var fromEnv = Environment.GetEnvironmentVariable(StorageRootEnvVar);

        var root = !string.IsNullOrWhiteSpace(configured)
            ? configured
            : fromEnv;

        if (string.IsNullOrWhiteSpace(root))
        {
            root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DimonSmart",
                "OllamaChat");
        }

        return Path.GetFullPath(root);
    }
}

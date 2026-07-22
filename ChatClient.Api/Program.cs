using ChatClient.Api;
using ChatClient.Api.Diagnostics;
using ChatClient.Api.Services;
using ChatClient.Api.Services.BuiltIn;
using ChatClient.Api.Startup;
using ChatClient.Api.VoiceInput;
using ChatClient.Application.Services.Agentic;
using Serilog;
using Serilog.Events;
using System.IO;
using System.Text;

Console.OutputEncoding = Encoding.UTF8;
Console.InputEncoding = Encoding.UTF8;

if (await BuiltInMcpServerHost.TryRunAsync(args))
{
    return;
}

var runFromSelfContainedLayout = !string.Equals(
    Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"),
    Environments.Development,
    StringComparison.OrdinalIgnoreCase);
var appBaseDirectory = ResolveApplicationBaseDirectory();

if (runFromSelfContainedLayout)
{
    Directory.SetCurrentDirectory(appBaseDirectory);
}

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("ModelContextProtocol", LogEventLevel.Warning)
    .MinimumLevel.Override("System.Net.Http.HttpClient", LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.Extensions.Http", LogEventLevel.Warning)
    .WriteTo.Console()
    .WriteTo.File("Logs/ollamachat-.log", rollingInterval: RollingInterval.Day, fileSizeLimitBytes: 10_000_000, rollOnFileSizeLimit: true, retainedFileCountLimit: 5)
    .WriteTo.Debug()
    .CreateLogger();

try
{
    SemaphoreFullExceptionDebugHook.Register();
    ExceptionDiagnosticsHook.Register();

    var builder = runFromSelfContainedLayout
        ? WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = appBaseDirectory,
            WebRootPath = Path.Combine(appBaseDirectory, "wwwroot")
        })
        : WebApplication.CreateBuilder(args);
    builder.Host.UseSerilog();

    builder.Configuration.AddUserSecrets<Program>(optional: true);

    builder.Services.AddApplicationServices(builder.Configuration, builder.Environment);

    var app = builder.Build();
    ExceptionDiagnosticsHook.TrackLifetime(app.Lifetime);
    await app.InitializeApplicationAsync();

    if (app.Environment.IsDevelopment())
    {
        app.UseDeveloperExceptionPage();
    }
    else
    {
        app.UseExceptionHandler("/Error");
        app.UseHsts();
    }

    app.UseStaticFiles();

    app.UseRouting();

    app.MapVoiceInputEndpoints();
    app.MapRazorPages();
    app.MapBlazorHub();
    app.MapFallbackToPage("/_Host");
    app.RegisterBrowserLaunch();

    await app.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application terminated unexpectedly.");
    throw;
}
finally
{
    ExceptionDiagnosticsHook.FlushSummary();
    Log.CloseAndFlush();
}

static string ResolveApplicationBaseDirectory()
{
    var processPath = Environment.ProcessPath;
    if (string.IsNullOrWhiteSpace(processPath))
    {
        return AppContext.BaseDirectory;
    }

    try
    {
        var processFile = new FileInfo(processPath);
        var targetFile = processFile.ResolveLinkTarget(returnFinalTarget: true);
        if (targetFile is FileInfo resolvedFile && !string.IsNullOrWhiteSpace(resolvedFile.DirectoryName))
        {
            return resolvedFile.DirectoryName;
        }
    }
    catch
    {
    }

    var processDirectory = Path.GetDirectoryName(processPath);
    return string.IsNullOrWhiteSpace(processDirectory)
        ? AppContext.BaseDirectory
        : processDirectory;
}

using Serilog;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ChatClient.Api.Services
{
    /// <summary>
    /// Helper service for launching the system's default browser
    /// </summary>
    public static class BrowserLaunchService
    {
        /// <summary>
        /// Display application information and launch the default browser
        /// </summary>
        /// <param name="httpUrl">Full HTTP URL to display and open</param>
        /// <param name="httpsUrl">Full HTTPS URL to display</param>
        /// <param name="delayMs">Delay in milliseconds before launching browser</param>
        public static void DisplayInfoAndLaunchBrowser(string httpUrl, string httpsUrl, int delayMs = 1500)
        {
            Log.Information(string.Empty);
            Log.Information("=================================================");
            Log.Information(
                "OllamaChat is running. HTTP: {HttpUrl} HTTPS: {HttpsUrl}",
                httpUrl,
                httpsUrl);
            Log.Information("=================================================");
            Log.Information(string.Empty);

            Task.Run(async () =>
            {
                await Task.Delay(delayMs);
                OpenBrowser(httpUrl);
            });
        }

        /// <summary>
        /// Open the default system browser to the specified URL
        /// </summary>
        /// <param name="url">URL to open</param>
        public static void OpenBrowser(string url)
        {
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    Process.Start("xdg-open", url);
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                {
                    Process.Start("open", url);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to open browser at {Url}", url);
            }
        }
    }
}

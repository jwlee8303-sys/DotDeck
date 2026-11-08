using System.IO;
using Microsoft.Web.WebView2.Core;

namespace DotDeck.App.Infrastructure.Windows;

/// <summary>
/// One shared WebView2 environment (user data under <c>%LOCALAPPDATA%\DotDeck\WebView2</c>, DESIGN §3) and the
/// private folder holding generated preview pages.
/// </summary>
public static class WebViewEnvironment
{
    private static string? s_userDataFolder;
    private static Task<CoreWebView2Environment>? s_environment;

    public static string PagesFolder { get; private set; } = string.Empty;

    public static void Initialize(string userDataFolder, string pagesFolder)
    {
        s_userDataFolder = userDataFolder;
        PagesFolder = pagesFolder;
        try
        {
            // Pages are per-view temporary files; anything left over is from an earlier run.
            if (Directory.Exists(pagesFolder))
            {
                foreach (var file in Directory.EnumerateFiles(pagesFolder, "*.html"))
                {
                    File.Delete(file);
                }
            }

            Directory.CreateDirectory(pagesFolder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The preview reports its own failure when it cannot write a page.
        }
    }

    /// <summary>Installed runtime version, or null when the WebView2 runtime is missing.</summary>
    public static string? RuntimeVersion
    {
        get
        {
            try
            {
                return CoreWebView2Environment.GetAvailableBrowserVersionString();
            }
            catch (WebView2RuntimeNotFoundException)
            {
                return null;
            }
        }
    }

    public static Task<CoreWebView2Environment> GetAsync()
    {
        if (s_userDataFolder is null)
        {
            throw new InvalidOperationException("WebViewEnvironment is not initialized.");
        }

        return s_environment ??= CoreWebView2Environment.CreateAsync(
            browserExecutableFolder: null,
            userDataFolder: s_userDataFolder,
            options: new CoreWebView2EnvironmentOptions { AreBrowserExtensionsEnabled = false });
    }
}

using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Windows;
using TurboGet.Services;

namespace TurboGet;

public partial class App : Application
{
    private const string SingleInstanceMutexName = @"Local\TurboGet_SingleInstance_Mutex";
    private Mutex? _instanceMutex;

    public static string? StartupTorrentArg { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        var torrentArg = e.Args.FirstOrDefault(a =>
            !string.IsNullOrWhiteSpace(a) &&
            !a.StartsWith("--", StringComparison.Ordinal) &&
            (a.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase) ||
             (File.Exists(a) && a.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase))));

        _instanceMutex = new Mutex(true, SingleInstanceMutexName, out bool isFirstInstance);

        if (!isFirstInstance)
        {
            if (!string.IsNullOrWhiteSpace(torrentArg))
            {
                TryForwardToRunningInstance(torrentArg);
            }
            else
            {
                TryFocusRunningInstance();
            }

            _instanceMutex.Dispose();
            _instanceMutex = null;
            Shutdown(0);
            return;
        }

        if (!string.IsNullOrWhiteSpace(torrentArg))
        {
            StartupTorrentArg = torrentArg;
        }

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _instanceMutex?.ReleaseMutex();
            _instanceMutex?.Dispose();
        }
        catch { }
        _instanceMutex = null;
        base.OnExit(e);
    }

    private static bool TryForwardToRunningInstance(string torrentSource)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromMilliseconds(900) };
            var json = JsonSerializer.Serialize(new
            {
                url = torrentSource,
                type = "torrent",
                focus = true
            });
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            var resp = client.PostAsync($"{BrowserBridgeServer.BasePrefix}api/download", content)
                .GetAwaiter().GetResult();
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private static void TryFocusRunningInstance()
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromMilliseconds(900) };
            client.PostAsync($"{BrowserBridgeServer.BasePrefix}api/focus", null)
                .GetAwaiter().GetResult();
        }
        catch
        {
            // Ignore if bridge isn't ready yet
        }
    }
}

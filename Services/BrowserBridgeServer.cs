using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using TurboGet.Models;

namespace TurboGet.Services;

public sealed class BrowserBridgeServer : IDisposable
{
    public const int BridgePort = 47821;
    public const string BasePrefix = "http://127.0.0.1:47821/";

    private readonly DownloadManagerService _manager;
    private readonly Action<DownloadItem>? _onDownloadAdded;
    private readonly Action? _onFocusRequested;
    private readonly Func<string, Task>? _onTorrentPromptRequested;
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();
    private Task? _listenTask;

    public bool IsRunning { get; private set; }
    public string StatusText { get; private set; } = "Bridge: Chưa chạy";

    public BrowserBridgeServer(
        DownloadManagerService manager,
        Action<DownloadItem>? onDownloadAdded = null,
        Action? onFocusRequested = null,
        Func<string, Task>? onTorrentPromptRequested = null)
    {
        _manager = manager;
        _onDownloadAdded = onDownloadAdded;
        _onFocusRequested = onFocusRequested;
        _onTorrentPromptRequested = onTorrentPromptRequested;
    }

    public void Start()
    {
        if (IsRunning) return;

        try
        {
            _listener.Prefixes.Add(BasePrefix);
            _listener.Start();
            IsRunning = true;
            StatusText = $"🌐 Extension Bridge: Bật (:{BridgePort})";
            _listenTask = Task.Run(() => ListenLoopAsync(_cts.Token));
        }
        catch (Exception ex)
        {
            IsRunning = false;
            StatusText = $"🌐 Extension Bridge: Lỗi ({ex.Message})";
        }
    }

    private async Task ListenLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener.IsListening)
        {
            HttpListenerContext? ctx = null;
            try
            {
                ctx = await _listener.GetContextAsync().WaitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (HttpListenerException)
            {
                break;
            }
            catch
            {
                continue;
            }

            if (ctx is not null)
            {
                _ = Task.Run(() => HandleRequestAsync(ctx), ct);
            }
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var res = ctx.Response;

        res.Headers["Access-Control-Allow-Origin"] = "*";
        res.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS";
        res.Headers["Access-Control-Allow-Headers"] = "Content-Type";

        try
        {
            if (req.HttpMethod.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase))
            {
                res.StatusCode = 204;
                res.Close();
                return;
            }

            var path = req.Url?.AbsolutePath?.TrimEnd('/') ?? string.Empty;

            if (req.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
                path.Equals("/api/ping", StringComparison.OrdinalIgnoreCase))
            {
                var snapshot = _manager.Downloads.ToList();
                var activeList = snapshot.Where(d => d.Status is DownloadStatus.Downloading or DownloadStatus.Queued).ToList();
                double avgProgress = activeList.Count > 0 ? activeList.Average(d => d.ProgressPercent) : 0;
                long totalSpeed = activeList.Sum(d => d.DownloadSpeedBytesPerSec);

                await WriteJsonAsync(res, 200, new
                {
                    ok = true,
                    app = "TurboGet",
                    version = "1.0",
                    activeDownloads = activeList.Count,
                    averageProgress = Math.Round(avgProgress, 1),
                    totalSpeedText = totalSpeed > 0 ? FormatHelper.FormatSpeed(totalSpeed) : "0 B/s",
                    defaultThreads = _manager.Settings.DefaultThreadCount
                });
                return;
            }

            if ((req.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) ||
                 req.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase)) &&
                path.Equals("/api/focus", StringComparison.OrdinalIgnoreCase))
            {
                var dispatcher = Application.Current?.Dispatcher;
                if (dispatcher is not null)
                {
                    await dispatcher.InvokeAsync(() => _onFocusRequested?.Invoke());
                }
                await WriteJsonAsync(res, 200, new { ok = true });
                return;
            }

            if (req.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
                path.Equals("/api/downloads", StringComparison.OrdinalIgnoreCase))
            {
                var snapshot = _manager.Downloads.Take(10).Select(d => new
                {
                    id = d.Id.ToString(),
                    name = d.Name,
                    type = d.TypeText,
                    status = d.Status.ToString(),
                    statusText = d.StatusText,
                    progress = Math.Round(d.ProgressPercent, 1),
                    speed = d.FormattedSpeed,
                    size = d.FormattedSize,
                    eta = d.EtaText,
                    canPause = d.CanPause,
                    canResume = d.CanResume
                }).ToList();

                var activeList = _manager.Downloads.Where(d => d.Status is DownloadStatus.Downloading or DownloadStatus.Queued).ToList();
                long totalSpeed = activeList.Sum(d => d.DownloadSpeedBytesPerSec);
                double avgProgress = activeList.Count > 0 ? activeList.Average(d => d.ProgressPercent) : 0;

                await WriteJsonAsync(res, 200, new
                {
                    ok = true,
                    activeCount = activeList.Count,
                    averageProgress = Math.Round(avgProgress, 1),
                    totalSpeedText = totalSpeed > 0 ? FormatHelper.FormatSpeed(totalSpeed) : "0 B/s",
                    downloads = snapshot
                });
                return;
            }

            if (req.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                path.Equals("/api/action", StringComparison.OrdinalIgnoreCase))
            {
                using var reader = new StreamReader(req.InputStream, req.ContentEncoding ?? Encoding.UTF8);
                var body = await reader.ReadToEndAsync();
                var payload = JsonSerializer.Deserialize<BrowserActionRequest>(body, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                if (payload is null || !Guid.TryParse(payload.Id, out var id))
                {
                    await WriteJsonAsync(res, 400, new { ok = false, error = "ID không hợp lệ." });
                    return;
                }

                var target = _manager.Downloads.FirstOrDefault(d => d.Id == id);
                if (target is null)
                {
                    await WriteJsonAsync(res, 404, new { ok = false, error = "Không tìm thấy mục tải." });
                    return;
                }

                var dispatcher = Application.Current?.Dispatcher;
                if (dispatcher is not null)
                {
                    await dispatcher.InvokeAsync(async () =>
                    {
                        if (string.Equals(payload.Action, "pause", StringComparison.OrdinalIgnoreCase))
                            await _manager.PauseAsync(target);
                        else if (string.Equals(payload.Action, "resume", StringComparison.OrdinalIgnoreCase))
                            await _manager.StartOrResumeAsync(target);
                    }).Task.Unwrap();
                }

                await WriteJsonAsync(res, 200, new { ok = true });
                return;
            }

            if (req.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                path.Equals("/api/download", StringComparison.OrdinalIgnoreCase))
            {
                using var reader = new StreamReader(req.InputStream, req.ContentEncoding ?? Encoding.UTF8);
                var body = await reader.ReadToEndAsync();
                var payload = JsonSerializer.Deserialize<BrowserDownloadRequest>(body, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                if (payload is null || string.IsNullOrWhiteSpace(payload.Url))
                {
                    await WriteJsonAsync(res, 400, new { ok = false, error = "Thiếu URL tải xuống." });
                    return;
                }

                string url = payload.Url.Trim();
                bool isLocalTorrent = File.Exists(url) && url.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase);
                bool isMagnet = url.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase);
                bool isHttpUrl = url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                                 url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
                bool isRemoteTorrent = isHttpUrl && (
                    string.Equals(payload.Type, "torrent", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(payload.Mime, "application/x-bittorrent", StringComparison.OrdinalIgnoreCase) ||
                    (payload.Filename?.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase) == true) ||
                    url.Split('?')[0].EndsWith(".torrent", StringComparison.OrdinalIgnoreCase));

                string? downloadedTorrentFile = null;
                if (isRemoteTorrent)
                {
                    downloadedTorrentFile = await DownloadTorrentMetadataFileAsync(url, payload);
                }

                bool isTorrent = isLocalTorrent || isMagnet || downloadedTorrentFile is not null;
                string torrentSource = downloadedTorrentFile ?? url;

                if (isTorrent && _manager.Settings.AskSaveLocationForTorrent && _onTorrentPromptRequested is not null)
                {
                    var uiDispatcher = Application.Current?.Dispatcher;
                    if (uiDispatcher is not null)
                    {
                        _ = uiDispatcher.InvokeAsync(async () =>
                        {
                            _onFocusRequested?.Invoke();
                            await _onTorrentPromptRequested(torrentSource);
                        });
                    }

                    string displayName = isMagnet
                        ? "Link Magnet"
                        : Path.GetFileNameWithoutExtension(torrentSource);

                    await WriteJsonAsync(res, 200, new
                    {
                        ok = true,
                        name = displayName,
                        type = "torrent",
                        prompting = true
                    });
                    return;
                }

                DownloadItem? added = null;
                var dispatcher = Application.Current?.Dispatcher;
                if (dispatcher is not null)
                {
                    await dispatcher.InvokeAsync(async () =>
                    {
                        if (isTorrent)
                        {
                            added = await _manager.AddTorrentDownloadAsync(torrentSource);
                        }
                        else
                        {
                            added = await _manager.AddHttpDownloadAsync(
                                url: url,
                                fileName: payload.Filename,
                                referer: payload.Referer,
                                cookieHeader: payload.Cookie,
                                userAgent: payload.UserAgent);
                        }

                        if (added is not null)
                            _onDownloadAdded?.Invoke(added);

                        if (payload.Focus == true)
                            _onFocusRequested?.Invoke();
                    }).Task.Unwrap();
                }

                await WriteJsonAsync(res, 200, new
                {
                    ok = true,
                    id = added?.Id,
                    name = added?.Name,
                    type = isTorrent ? "torrent" : "http"
                });
                return;
            }

            await WriteJsonAsync(res, 404, new { ok = false, error = "Not Found" });
        }
        catch (Exception ex)
        {
            try
            {
                await WriteJsonAsync(res, 500, new { ok = false, error = ex.Message });
            }
            catch { }
        }
    }

    private static async Task<string?> DownloadTorrentMetadataFileAsync(string url, BrowserDownloadRequest payload)
    {
        try
        {
            var torrentsDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TurboGet",
                "Torrents");
            Directory.CreateDirectory(torrentsDir);

            using var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = true,
                AutomaticDecompression = DecompressionMethods.All
            };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
            using var req = new HttpRequestMessage(HttpMethod.Get, url);

            if (!string.IsNullOrWhiteSpace(payload.Referer) && Uri.TryCreate(payload.Referer, UriKind.Absolute, out var refUri))
                req.Headers.Referrer = refUri;
            if (!string.IsNullOrWhiteSpace(payload.Cookie))
                req.Headers.TryAddWithoutValidation("Cookie", payload.Cookie);
            if (!string.IsNullOrWhiteSpace(payload.UserAgent))
                req.Headers.TryAddWithoutValidation("User-Agent", payload.UserAgent);

            using var resp = await client.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return null;

            var bytes = await resp.Content.ReadAsByteArrayAsync();
            // Bencoded torrent files start with 'd'
            if (bytes.Length == 0 || bytes[0] != (byte)'d') return null;

            var rawName = !string.IsNullOrWhiteSpace(payload.Filename)
                ? Path.GetFileName(payload.Filename)
                : $"torrent_{DateTime.Now:yyyyMMdd_HHmmss}.torrent";
            if (!rawName.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase))
                rawName += ".torrent";

            var safeName = FormatHelper.SanitizeFileName(rawName);
            var outPath = Path.Combine(torrentsDir, $"{DateTime.Now:yyyyMMdd_HHmmss}_{safeName}");
            await File.WriteAllBytesAsync(outPath, bytes);
            return outPath;
        }
        catch
        {
            return null;
        }
    }

    private static async Task WriteJsonAsync(HttpListenerResponse res, int statusCode, object obj)
    {
        var json = JsonSerializer.Serialize(obj);
        var bytes = Encoding.UTF8.GetBytes(json);
        res.StatusCode = statusCode;
        res.ContentType = "application/json; charset=utf-8";
        res.ContentLength64 = bytes.Length;
        await res.OutputStream.WriteAsync(bytes);
        res.Close();
    }

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            if (_listener.IsListening)
                _listener.Stop();
            _listener.Close();
        }
        catch { }
        _cts.Dispose();
        IsRunning = false;
    }

    private sealed class BrowserDownloadRequest
    {
        [JsonPropertyName("url")]
        public string? Url { get; set; }

        [JsonPropertyName("filename")]
        public string? Filename { get; set; }

        [JsonPropertyName("referer")]
        public string? Referer { get; set; }

        [JsonPropertyName("cookie")]
        public string? Cookie { get; set; }

        [JsonPropertyName("userAgent")]
        public string? UserAgent { get; set; }

        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("mime")]
        public string? Mime { get; set; }

        [JsonPropertyName("focus")]
        public bool? Focus { get; set; }
    }

    private sealed class BrowserActionRequest
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("action")]
        public string? Action { get; set; }
    }
}

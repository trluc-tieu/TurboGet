using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Windows;
using Microsoft.Win32.SafeHandles;
using TurboGet.Models;

namespace TurboGet.Services;

public sealed class HttpDownloadEngine : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _activeDownloads = new();
    private readonly Func<int> _getSpeedLimitKbps;
    private readonly Action _onStateChanged;

    public HttpDownloadEngine(Func<int> getSpeedLimitKbps, Action onStateChanged)
    {
        _getSpeedLimitKbps = getSpeedLimitKbps;
        _onStateChanged = onStateChanged;

        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10,
            AutomaticDecompression = DecompressionMethods.None,
            MaxConnectionsPerServer = 64,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(20)
        };

        _httpClient = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36 TurboGet/1.0");
    }

    public bool IsRunning(Guid id) => _activeDownloads.ContainsKey(id);

    public Task StartOrResumeAsync(DownloadItem item)
    {
        if (_activeDownloads.ContainsKey(item.Id))
            return Task.CompletedTask;

        var cts = new CancellationTokenSource();
        if (!_activeDownloads.TryAdd(item.Id, cts))
        {
            cts.Dispose();
            return Task.CompletedTask;
        }

        _ = Task.Run(() => RunDownloadAsync(item, cts.Token));
        return Task.CompletedTask;
    }

    public void Pause(Guid id)
    {
        if (_activeDownloads.TryRemove(id, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
        }
    }

    public void PauseAll()
    {
        foreach (var id in _activeDownloads.Keys.ToList())
        {
            Pause(id);
        }
    }

    private async Task RunDownloadAsync(DownloadItem item, CancellationToken ct)
    {
        List<SegmentInfo>? segmentSnapshot = null;
        long[]? segmentDownloaded = null;

        try
        {
            await RunOnUiAsync(() =>
            {
                item.ErrorMessage = string.Empty;
                item.Status = DownloadStatus.Downloading;
            });

            bool isNewDownload = item.Segments.Count == 0 || string.IsNullOrWhiteSpace(item.FilePath);

            if (isNewDownload)
            {
                var probe = await ProbeUrlAsync(item, ct);
                var fileName = !string.IsNullOrWhiteSpace(item.Name) && item.Name != item.Source
                    ? item.Name
                    : probe.FileName;

                var finalPath = FormatHelper.GetUniqueFilePath(item.SaveDirectory, fileName);

                int threadCount = Math.Clamp(item.ThreadCount, 1, 32);
                // Only split into multiple segments if server supports Range and file is >= 512 KB
                bool useMultiThread = probe.SupportsRange && probe.ContentLength >= 512 * 1024;
                int actualSegments = useMultiThread ? threadCount : 1;

                var segments = new List<SegmentInfo>();
                if (probe.ContentLength > 0)
                {
                    long chunkSize = probe.ContentLength / actualSegments;
                    for (int i = 0; i < actualSegments; i++)
                    {
                        long start = i * chunkSize;
                        long end = (i == actualSegments - 1) ? probe.ContentLength - 1 : (start + chunkSize - 1);
                        segments.Add(new SegmentInfo
                        {
                            Index = i,
                            StartByte = start,
                            EndByte = end,
                            DownloadedBytes = 0,
                            StatusText = "Đang kết nối..."
                        });
                    }
                }
                else
                {
                    segments.Add(new SegmentInfo
                    {
                        Index = 0,
                        StartByte = 0,
                        EndByte = -1,
                        DownloadedBytes = 0,
                        StatusText = "Đang tải..."
                    });
                }

                await RunOnUiAsync(() =>
                {
                    item.Name = Path.GetFileName(finalPath);
                    item.FilePath = finalPath;
                    item.TotalBytes = probe.ContentLength;
                    item.SupportsResume = probe.SupportsRange;
                    item.ThreadCount = actualSegments;
                    item.Segments.Clear();
                    foreach (var seg in segments)
                        item.Segments.Add(seg);
                });
            }
            else
            {
                // Resuming: if server didn't support resume, reset segment progress
                if (!item.SupportsResume)
                {
                    await RunOnUiAsync(() =>
                    {
                        foreach (var seg in item.Segments)
                        {
                            seg.DownloadedBytes = 0;
                            seg.StatusText = "Tải lại...";
                        }
                        item.DownloadedBytes = 0;
                        item.ProgressPercent = 0;
                    });
                }
            }

            Directory.CreateDirectory(item.SaveDirectory);
            string partPath = item.FilePath + ".tgpart";

            // Ensure .tgpart exists (if resuming and file was deleted externally, reset)
            if (!File.Exists(partPath) && item.DownloadedBytes > 0)
            {
                await RunOnUiAsync(() =>
                {
                    foreach (var seg in item.Segments) seg.DownloadedBytes = 0;
                    item.DownloadedBytes = 0;
                    item.ProgressPercent = 0;
                });
            }

            segmentSnapshot = item.Segments.ToList();
            segmentDownloaded = segmentSnapshot.Select(s => s.DownloadedBytes).ToArray();
            long[] segmentLastTickBytes = segmentSnapshot.Select(s => s.DownloadedBytes).ToArray();
            string[] segmentStatus = segmentSnapshot.Select(s => s.StatusText).ToArray();

            using var fileHandle = File.OpenHandle(
                partPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.ReadWrite,
                FileOptions.Asynchronous);

            if (item.TotalBytes > 0 && RandomAccess.GetLength(fileHandle) < item.TotalBytes)
            {
                RandomAccess.SetLength(fileHandle, item.TotalBytes);
            }

            using var monitorCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var monitorTask = Task.Run(async () =>
            {
                var sw = Stopwatch.StartNew();
                long prevTotal = segmentDownloaded.Sum();
                while (!monitorCts.Token.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(300, monitorCts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    double elapsedSec = Math.Max(0.1, sw.Elapsed.TotalSeconds);
                    sw.Restart();

                    long currentTotal = 0;
                    var segSpeeds = new long[segmentSnapshot.Count];
                    for (int i = 0; i < segmentSnapshot.Count; i++)
                    {
                        long cur = Volatile.Read(ref segmentDownloaded[i]);
                        long prev = segmentLastTickBytes[i];
                        segmentLastTickBytes[i] = cur;
                        segSpeeds[i] = (long)((cur - prev) / elapsedSec);
                        currentTotal += cur;
                    }

                    long totalSpeed = (long)((currentTotal - prevTotal) / elapsedSec);
                    prevTotal = currentTotal;

                    long totalBytes = item.TotalBytes;
                    double pct = totalBytes > 0 ? (double)currentTotal / totalBytes * 100.0 : 0;
                    string eta = totalBytes > 0 ? FormatHelper.FormatEta(totalBytes - currentTotal, totalSpeed) : "-";

                    RunOnUi(() =>
                    {
                        item.DownloadedBytes = currentTotal;
                        item.ProgressPercent = pct;
                        item.DownloadSpeedBytesPerSec = totalSpeed;
                        item.EtaText = eta;

                        for (int i = 0; i < segmentSnapshot.Count; i++)
                        {
                            segmentSnapshot[i].DownloadedBytes = Volatile.Read(ref segmentDownloaded[i]);
                            segmentSnapshot[i].SpeedBytesPerSec = segSpeeds[i];
                            segmentSnapshot[i].StatusText = segmentStatus[i];
                        }
                    });
                }
            }, monitorCts.Token);

            // Download all segments in parallel
            var workerTasks = new List<Task>();
            int activeSegmentCount = segmentSnapshot.Count;

            for (int i = 0; i < segmentSnapshot.Count; i++)
            {
                int idx = i;
                var seg = segmentSnapshot[i];
                workerTasks.Add(DownloadSegmentAsync(
                    item,
                    fileHandle,
                    seg,
                    idx,
                    activeSegmentCount,
                    item.SupportsResume && item.TotalBytes > 0,
                    segmentDownloaded,
                    segmentStatus,
                    ct));
            }

            await Task.WhenAll(workerTasks);
            monitorCts.Cancel();
            await monitorTask;

            // Final flush to UI before rename
            fileHandle.Dispose();

            if (File.Exists(item.FilePath))
                File.Delete(item.FilePath);
            File.Move(partPath, item.FilePath);

            long finalBytes = new FileInfo(item.FilePath).Length;
            await RunOnUiAsync(() =>
            {
                for (int i = 0; i < segmentSnapshot.Count; i++)
                {
                    segmentSnapshot[i].DownloadedBytes = Volatile.Read(ref segmentDownloaded[i]);
                    segmentSnapshot[i].SpeedBytesPerSec = 0;
                    segmentSnapshot[i].StatusText = "Hoàn thành";
                }
                item.TotalBytes = finalBytes;
                item.DownloadedBytes = finalBytes;
                item.ProgressPercent = 100;
                item.DownloadSpeedBytesPerSec = 0;
                item.EtaText = "00:00";
                item.Status = DownloadStatus.Completed;
            });
            _onStateChanged();
        }
        catch (OperationCanceledException)
        {
            await RunOnUiAsync(() =>
            {
                if (segmentSnapshot is not null && segmentDownloaded is not null)
                {
                    long total = 0;
                    for (int i = 0; i < segmentSnapshot.Count; i++)
                    {
                        long cur = Volatile.Read(ref segmentDownloaded[i]);
                        segmentSnapshot[i].DownloadedBytes = cur;
                        total += cur;
                    }
                    item.DownloadedBytes = total;
                    if (item.TotalBytes > 0)
                        item.ProgressPercent = (double)total / item.TotalBytes * 100.0;
                }

                item.DownloadSpeedBytesPerSec = 0;
                item.EtaText = "-";
                foreach (var seg in item.Segments)
                {
                    seg.SpeedBytesPerSec = 0;
                    if (seg.ProgressPercent < 100)
                        seg.StatusText = "Tạm dừng";
                }
                item.Status = DownloadStatus.Paused;
            });
            _onStateChanged();
        }
        catch (Exception ex)
        {
            await RunOnUiAsync(() =>
            {
                item.DownloadSpeedBytesPerSec = 0;
                item.EtaText = "-";
                item.ErrorMessage = ex.Message;
                foreach (var seg in item.Segments)
                {
                    seg.SpeedBytesPerSec = 0;
                }
                item.Status = DownloadStatus.Error;
            });
            _onStateChanged();
        }
        finally
        {
            if (_activeDownloads.TryRemove(item.Id, out var cts))
            {
                cts.Dispose();
            }
        }
    }

    private async Task DownloadSegmentAsync(
        DownloadItem item,
        SafeFileHandle fileHandle,
        SegmentInfo seg,
        int idx,
        int totalSegments,
        bool useRange,
        long[] segmentDownloaded,
        string[] segmentStatus,
        CancellationToken ct)
    {
        const int maxRetries = 5;
        int attempt = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            long alreadyDownloaded = Volatile.Read(ref segmentDownloaded[idx]);
            if (useRange && seg.TotalBytes > 0 && alreadyDownloaded >= seg.TotalBytes)
            {
                segmentStatus[idx] = "Hoàn thành";
                return;
            }

            try
            {
                segmentStatus[idx] = attempt == 0 ? "Đang tải" : $"Thử lại ({attempt}/{maxRetries})";

                using var request = CreateRequest(HttpMethod.Get, item);
                long writeOffset = 0;

                // Only send Range header when downloading multiple segments or resuming mid-file.
                // Sending Range: bytes=0-(len-1) on a 1-segment fresh download breaks some PHP download scripts.
                bool sendRangeHeader = useRange && (totalSegments > 1 || alreadyDownloaded > 0);
                if (sendRangeHeader)
                {
                    long rangeStart = seg.StartByte + alreadyDownloaded;
                    long rangeEnd = seg.EndByte;
                    request.Headers.Range = new RangeHeaderValue(rangeStart, rangeEnd);
                    writeOffset = rangeStart;
                }
                else
                {
                    Volatile.Write(ref segmentDownloaded[idx], 0);
                    writeOffset = 0;
                }

                using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();

                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                var buffer = new byte[64 * 1024];
                var throttleSw = Stopwatch.StartNew();
                long bytesInWindow = 0;

                while (true)
                {
                    int read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
                    if (read == 0) break;

                    await RandomAccess.WriteAsync(fileHandle, buffer.AsMemory(0, read), writeOffset, ct);
                    writeOffset += read;
                    Interlocked.Add(ref segmentDownloaded[idx], read);

                    // Apply global speed limit divided across segments if configured
                    int limitKbps = _getSpeedLimitKbps();
                    if (limitKbps > 0)
                    {
                        bytesInWindow += read;
                        double perSegBytesPerSec = Math.Max(4096, (limitKbps * 1024.0) / Math.Max(1, totalSegments));
                        double expectedMs = (bytesInWindow / perSegBytesPerSec) * 1000.0;
                        double actualMs = throttleSw.Elapsed.TotalMilliseconds;
                        if (expectedMs > actualMs + 15)
                        {
                            int delayMs = (int)Math.Min(1000, expectedMs - actualMs);
                            await Task.Delay(delayMs, ct);
                        }
                        if (throttleSw.ElapsedMilliseconds >= 1000)
                        {
                            throttleSw.Restart();
                            bytesInWindow = 0;
                        }
                    }
                }

                // Verify we received the full expected segment if size is known
                if (seg.TotalBytes > 0 && Volatile.Read(ref segmentDownloaded[idx]) < seg.TotalBytes)
                {
                    throw new IOException($"Kết nối bị ngắt sớm ({Volatile.Read(ref segmentDownloaded[idx])}/{seg.TotalBytes} bytes).");
                }

                segmentStatus[idx] = "Hoàn thành";
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception) when (attempt < maxRetries)
            {
                attempt++;
                segmentStatus[idx] = $"Lỗi, thử lại ({attempt}/{maxRetries})";
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(8, attempt * 2)), ct);
            }
        }
    }

    private async Task<ProbeResult> ProbeUrlAsync(DownloadItem item, CancellationToken ct)
    {
        string url = item.Source;
        long contentLength = -1;
        bool supportsRange = false;
        string? fileName = null;

        // Try HEAD first
        try
        {
            using var headReq = CreateRequest(HttpMethod.Head, item);
            using var headResp = await _httpClient.SendAsync(headReq, HttpCompletionOption.ResponseHeadersRead, ct);
            if (headResp.IsSuccessStatusCode)
            {
                contentLength = headResp.Content.Headers.ContentLength ?? -1;
                supportsRange = headResp.Headers.AcceptRanges.Any(r =>
                    string.Equals(r, "bytes", StringComparison.OrdinalIgnoreCase));
                fileName = ExtractFileName(headResp, url);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            // Fallback to Range GET below
        }

        // Fallback probe with GET Range: bytes=0-0 if HEAD didn't give length or range support
        if (contentLength <= 0 || !supportsRange)
        {
            try
            {
                using var getReq = CreateRequest(HttpMethod.Get, item);
                getReq.Headers.Range = new RangeHeaderValue(0, 0);
                using var getResp = await _httpClient.SendAsync(getReq, HttpCompletionOption.ResponseHeadersRead, ct);
                if (getResp.StatusCode == HttpStatusCode.PartialContent)
                {
                    supportsRange = true;
                    if (getResp.Content.Headers.ContentRange?.Length is long totalLen)
                        contentLength = totalLen;
                    fileName ??= ExtractFileName(getResp, url);
                }
                else if (getResp.IsSuccessStatusCode)
                {
                    contentLength = getResp.Content.Headers.ContentLength ?? contentLength;
                    fileName ??= ExtractFileName(getResp, url);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                // Will proceed with unknown length single-stream download
            }
        }

        fileName ??= ExtractFileNameFromUrl(url);
        return new ProbeResult(contentLength, supportsRange, FormatHelper.SanitizeFileName(fileName));
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, DownloadItem item)
    {
        var req = new HttpRequestMessage(method, item.Source);
        if (!string.IsNullOrWhiteSpace(item.Referer) && Uri.TryCreate(item.Referer, UriKind.Absolute, out var refUri))
        {
            req.Headers.Referrer = refUri;
        }
        if (!string.IsNullOrWhiteSpace(item.CookieHeader))
        {
            req.Headers.TryAddWithoutValidation("Cookie", item.CookieHeader);
        }
        if (!string.IsNullOrWhiteSpace(item.CustomUserAgent))
        {
            req.Headers.UserAgent.Clear();
            req.Headers.TryAddWithoutValidation("User-Agent", item.CustomUserAgent);
        }
        return req;
    }

    private static string? ExtractFileName(HttpResponseMessage response, string fallbackUrl)
    {
        var cd = response.Content.Headers.ContentDisposition;
        var raw = cd?.FileNameStar ?? cd?.FileName;
        if (!string.IsNullOrWhiteSpace(raw))
        {
            return raw.Trim('"', '\'');
        }

        var finalUri = response.RequestMessage?.RequestUri?.ToString() ?? fallbackUrl;
        return ExtractFileNameFromUrl(finalUri);
    }

    private static string ExtractFileNameFromUrl(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var segment = Path.GetFileName(Uri.UnescapeDataString(uri.AbsolutePath));
            if (!string.IsNullOrWhiteSpace(segment) && segment != "/")
                return segment;
        }
        return $"download_{DateTime.Now:yyyyMMdd_HHmmss}.bin";
    }

    private static void RunOnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
            action();
        else
            dispatcher.BeginInvoke(action);
    }

    private static Task RunOnUiAsync(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }
        return dispatcher.InvokeAsync(action).Task;
    }

    public void Dispose()
    {
        PauseAll();
        _httpClient.Dispose();
    }

    private sealed record ProbeResult(long ContentLength, bool SupportsRange, string FileName);
}

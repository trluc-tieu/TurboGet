using System.Collections.Concurrent;
using System.IO;
using System.Windows;
using MonoTorrent;
using MonoTorrent.Client;
using TurboGet.Models;

namespace TurboGet.Services;

public sealed class TorrentDownloadEngine : IAsyncDisposable
{
    private readonly ClientEngine _engine;
    private readonly ConcurrentDictionary<Guid, (TorrentManager Manager, DownloadItem Item)> _torrents = new();
    private readonly Func<AppSettings> _getSettings;
    private readonly Action _onStateChanged;
    private readonly CancellationTokenSource _loopCts = new();
    private readonly Task _statsLoopTask;

    public TorrentDownloadEngine(Func<AppSettings> getSettings, Action onStateChanged)
    {
        _getSettings = getSettings;
        _onStateChanged = onStateChanged;

        var cacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TurboGet",
            "TorrentCache");
        Directory.CreateDirectory(cacheDir);

        var settingsBuilder = new EngineSettingsBuilder
        {
            CacheDirectory = cacheDir,
            AllowPortForwarding = true,
            AllowLocalPeerDiscovery = true,
            AutoSaveLoadDhtCache = true,
            AutoSaveLoadFastResume = true,
            AutoSaveLoadMagnetLinkMetadata = true,
            MaximumDownloadRate = Math.Max(0, _getSettings().SpeedLimitKbps * 1024)
        };

        _engine = new ClientEngine(settingsBuilder.ToSettings());
        _statsLoopTask = Task.Run(StatsLoopAsync);
    }

    public async Task ApplySpeedLimitAsync(int speedLimitKbps)
    {
        var updated = new EngineSettingsBuilder(_engine.Settings)
        {
            MaximumDownloadRate = Math.Max(0, speedLimitKbps * 1024)
        };
        await _engine.UpdateSettingsAsync(updated.ToSettings());
    }

    public async Task StartOrResumeAsync(DownloadItem item)
    {
        try
        {
            RunOnUi(() =>
            {
                item.ErrorMessage = string.Empty;
                item.Status = DownloadStatus.Downloading;
            });

            if (_torrents.TryGetValue(item.Id, out var existing))
            {
                await existing.Manager.StartAsync();
                return;
            }

            Directory.CreateDirectory(item.SaveDirectory);

            TorrentManager manager;
            if (item.Source.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
            {
                var magnet = MagnetLink.Parse(item.Source);
                manager = await _engine.AddAsync(magnet, item.SaveDirectory);
                if (string.IsNullOrWhiteSpace(item.Name) || item.Name.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
                {
                    var name = !string.IsNullOrWhiteSpace(magnet.Name)
                        ? magnet.Name
                        : $"Torrent_{magnet.InfoHashes.V1OrV2.ToHex()[..8]}";
                    RunOnUi(() => item.Name = name);
                }
            }
            else if (File.Exists(item.Source))
            {
                manager = await _engine.AddAsync(item.Source, item.SaveDirectory);
                if (manager.Torrent is not null)
                {
                    RunOnUi(() =>
                    {
                        item.Name = manager.Torrent.Name;
                        item.TotalBytes = manager.Torrent.Size;
                    });
                }
            }
            else
            {
                throw new FileNotFoundException("Không tìm thấy file .torrent hoặc link Magnet không hợp lệ.", item.Source);
            }

            _torrents[item.Id] = (manager, item);

            RunOnUi(() =>
            {
                item.FilePath = manager.ContainingDirectory;
            });

            await manager.StartAsync();
            _onStateChanged();
        }
        catch (Exception ex)
        {
            RunOnUi(() =>
            {
                item.ErrorMessage = ex.Message;
                item.Status = DownloadStatus.Error;
            });
            _onStateChanged();
        }
    }

    public async Task PauseAsync(Guid id)
    {
        if (_torrents.TryGetValue(id, out var entry))
        {
            await entry.Manager.PauseAsync();
            RunOnUi(() =>
            {
                entry.Item.DownloadSpeedBytesPerSec = 0;
                entry.Item.UploadSpeedBytesPerSec = 0;
                entry.Item.EtaText = "-";
                entry.Item.Status = DownloadStatus.Paused;
            });
            _onStateChanged();
        }
    }

    public async Task RemoveAsync(Guid id)
    {
        if (_torrents.TryRemove(id, out var entry))
        {
            try
            {
                if (entry.Manager.State != TorrentState.Stopped)
                    await entry.Manager.StopAsync();
                await _engine.RemoveAsync(entry.Manager);
            }
            catch
            {
                // Ignore teardown errors
            }
        }
    }

    private async Task StatsLoopAsync()
    {
        while (!_loopCts.Token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(500, _loopCts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            var settings = _getSettings();

            foreach (var kv in _torrents.ToArray())
            {
                var (manager, item) = kv.Value;

                try
                {
                    var state = manager.State;
                    double progress = manager.Progress;
                    long downRate = manager.Monitor.DownloadRate;
                    long upRate = manager.Monitor.UploadRate;
                    int seeds = manager.Peers.Seeds;
                    int leechs = manager.Peers.Leechs;
                    long totalBytes = manager.Torrent?.Size ?? item.TotalBytes;
                    long downloadedBytes = totalBytes > 0
                        ? (long)(totalBytes * (progress / 100.0))
                        : manager.Monitor.DataBytesReceived;

                    string? torrentName = manager.Torrent?.Name;
                    string containingDir = manager.ContainingDirectory;

                    // Auto-stop seeding if user prefers IDM-like completion
                    if (state == TorrentState.Seeding && settings.StopSeedingOnComplete)
                    {
                        await manager.StopAsync();
                        RunOnUi(() =>
                        {
                            item.TotalBytes = totalBytes;
                            item.DownloadedBytes = totalBytes;
                            item.ProgressPercent = 100;
                            item.DownloadSpeedBytesPerSec = 0;
                            item.UploadSpeedBytesPerSec = 0;
                            item.EtaText = "00:00";
                            item.Status = DownloadStatus.Completed;
                            if (!string.IsNullOrWhiteSpace(torrentName))
                                item.Name = torrentName;
                            item.FilePath = containingDir;
                        });
                        _onStateChanged();
                        continue;
                    }

                    var mappedStatus = state switch
                    {
                        TorrentState.Downloading or TorrentState.Metadata or TorrentState.Hashing or TorrentState.Starting => DownloadStatus.Downloading,
                        TorrentState.Seeding => DownloadStatus.Seeding,
                        TorrentState.Paused or TorrentState.HashingPaused => DownloadStatus.Paused,
                        TorrentState.Stopped or TorrentState.Stopping => progress >= 99.99 ? DownloadStatus.Completed : DownloadStatus.Paused,
                        TorrentState.Error => DownloadStatus.Error,
                        _ => item.Status
                    };

                    string eta = mappedStatus == DownloadStatus.Downloading && totalBytes > 0
                        ? FormatHelper.FormatEta(totalBytes - downloadedBytes, downRate)
                        : (mappedStatus is DownloadStatus.Completed or DownloadStatus.Seeding ? "00:00" : "-");

                    var filesSnapshot = manager.Files
                        .Select(f => (f.Path, f.Length, Pct: f.BitField.PercentComplete))
                        .ToList();

                    RunOnUi(() =>
                    {
                        if (!string.IsNullOrWhiteSpace(torrentName) && item.Name != torrentName)
                            item.Name = torrentName;

                        if (!string.IsNullOrWhiteSpace(containingDir))
                            item.FilePath = containingDir;

                        item.TotalBytes = totalBytes;
                        item.DownloadedBytes = downloadedBytes;
                        item.ProgressPercent = progress;
                        item.DownloadSpeedBytesPerSec = downRate;
                        item.UploadSpeedBytesPerSec = upRate;
                        item.Seeds = seeds;
                        item.Peers = leechs;
                        item.EtaText = eta;
                        item.Status = mappedStatus;

                        if (filesSnapshot.Count > 0)
                        {
                            if (item.TorrentFiles.Count != filesSnapshot.Count)
                            {
                                item.TorrentFiles.Clear();
                                foreach (var f in filesSnapshot)
                                {
                                    item.TorrentFiles.Add(new TorrentFileDetail
                                    {
                                        FilePath = f.Path,
                                        SizeBytes = f.Length,
                                        ProgressPercent = f.Pct
                                    });
                                }
                            }
                            else
                            {
                                for (int i = 0; i < filesSnapshot.Count; i++)
                                {
                                    item.TorrentFiles[i].ProgressPercent = filesSnapshot[i].Pct;
                                }
                            }
                        }
                    });
                }
                catch
                {
                    // Skip transient read errors
                }
            }
        }
    }

    private static void RunOnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
            action();
        else
            dispatcher.BeginInvoke(action);
    }

    public async ValueTask DisposeAsync()
    {
        _loopCts.Cancel();
        try { await _statsLoopTask; } catch { }
        _loopCts.Dispose();

        try
        {
            await _engine.StopAllAsync();
        }
        catch { }

        _engine.Dispose();
    }
}

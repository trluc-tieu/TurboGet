using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using TurboGet.Models;

namespace TurboGet.Services;

public sealed class DownloadManagerService : IAsyncDisposable
{
    private static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TurboGet");

    private static readonly string StateFilePath = Path.Combine(DataDir, "state.json");
    private static readonly string SettingsFilePath = Path.Combine(DataDir, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly HttpDownloadEngine _httpEngine;
    private readonly TorrentDownloadEngine _torrentEngine;
    private readonly object _saveLock = new();

    public ObservableCollection<DownloadItem> Downloads { get; } = new();
    public AppSettings Settings { get; private set; }
    public event Action<DownloadItem>? DownloadCompleted;

    public DownloadManagerService()
    {
        Directory.CreateDirectory(DataDir);
        Settings = LoadSettings();
        Settings.StartWithWindows = StartupHelper.IsEnabled();
        Directory.CreateDirectory(Settings.DefaultSaveDirectory);

        _httpEngine = new HttpDownloadEngine(
            () => Settings.SpeedLimitKbps,
            SaveState);

        _torrentEngine = new TorrentDownloadEngine(
            () => Settings,
            SaveState);

        LoadState();
    }

    private void AttachItemWatcher(DownloadItem item)
    {
        var lastStatus = item.Status;
        item.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(DownloadItem.Status)) return;
            var newStatus = item.Status;
            if (newStatus == DownloadStatus.Completed && lastStatus != DownloadStatus.Completed)
            {
                DownloadCompleted?.Invoke(item);
            }
            lastStatus = newStatus;
        };
    }

    public async Task<DownloadItem> AddHttpDownloadAsync(
        string url,
        string? saveDir = null,
        int? threadCount = null,
        bool startImmediately = true,
        string? fileName = null,
        string? referer = null,
        string? cookieHeader = null,
        string? userAgent = null)
    {
        var dir = string.IsNullOrWhiteSpace(saveDir) ? Settings.DefaultSaveDirectory : saveDir;
        var threads = Math.Clamp(threadCount ?? Settings.DefaultThreadCount, 1, 32);

        var item = new DownloadItem
        {
            Type = DownloadType.Http,
            Source = url.Trim(),
            Name = !string.IsNullOrWhiteSpace(fileName) ? FormatHelper.SanitizeFileName(Path.GetFileName(fileName)) : url.Trim(),
            SaveDirectory = dir,
            ThreadCount = threads,
            Referer = referer,
            CookieHeader = cookieHeader,
            CustomUserAgent = userAgent,
            Status = DownloadStatus.Queued
        };

        AttachItemWatcher(item);
        Downloads.Insert(0, item);
        SaveState();

        if (startImmediately)
        {
            await _httpEngine.StartOrResumeAsync(item);
        }

        return item;
    }

    public async Task<DownloadItem> AddTorrentDownloadAsync(string torrentPathOrMagnet, string? saveDir = null, bool startImmediately = true)
    {
        var defaultTorrentDir = string.IsNullOrWhiteSpace(Settings.DefaultTorrentSaveDirectory)
            ? Settings.DefaultSaveDirectory
            : Settings.DefaultTorrentSaveDirectory;
        var dir = string.IsNullOrWhiteSpace(saveDir) ? defaultTorrentDir : saveDir;
        var source = torrentPathOrMagnet.Trim();

        string initialName = source.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase)
            ? "Đang lấy metadata Magnet..."
            : Path.GetFileNameWithoutExtension(source);

        var item = new DownloadItem
        {
            Type = DownloadType.Torrent,
            Source = source,
            Name = initialName,
            SaveDirectory = dir,
            ThreadCount = 1,
            SupportsResume = true,
            Status = DownloadStatus.Queued
        };

        AttachItemWatcher(item);
        Downloads.Insert(0, item);
        SaveState();

        if (startImmediately)
        {
            await _torrentEngine.StartOrResumeAsync(item);
        }

        return item;
    }

    public async Task StartOrResumeAsync(DownloadItem item)
    {
        if (item.Status == DownloadStatus.Completed) return;

        if (item.Type == DownloadType.Http)
            await _httpEngine.StartOrResumeAsync(item);
        else
            await _torrentEngine.StartOrResumeAsync(item);
    }

    public async Task PauseAsync(DownloadItem item)
    {
        if (item.Type == DownloadType.Http)
        {
            _httpEngine.Pause(item.Id);
            if (item.Status == DownloadStatus.Queued)
                item.Status = DownloadStatus.Paused;
        }
        else
        {
            await _torrentEngine.PauseAsync(item.Id);
        }
        SaveState();
    }

    public async Task RemoveAsync(DownloadItem item, bool deleteFilesFromDisk = false)
    {
        if (item.Type == DownloadType.Http)
        {
            _httpEngine.Pause(item.Id);
        }
        else
        {
            await _torrentEngine.RemoveAsync(item.Id);
        }

        Downloads.Remove(item);

        if (deleteFilesFromDisk)
        {
            try
            {
                if (item.Type == DownloadType.Http && !string.IsNullOrWhiteSpace(item.FilePath))
                {
                    if (File.Exists(item.FilePath)) File.Delete(item.FilePath);
                    if (File.Exists(item.FilePath + ".tgpart")) File.Delete(item.FilePath + ".tgpart");
                }
            }
            catch
            {
                // Ignore file deletion errors (e.g. locked by another process)
            }
        }

        SaveState();
    }

    public async Task UpdateSettingsAsync(AppSettings newSettings)
    {
        Settings = newSettings;
        Directory.CreateDirectory(Settings.DefaultSaveDirectory);
        if (!string.IsNullOrWhiteSpace(Settings.DefaultTorrentSaveDirectory))
            Directory.CreateDirectory(Settings.DefaultTorrentSaveDirectory);
        StartupHelper.SetEnabled(Settings.StartWithWindows);
        SaveSettings();
        await _torrentEngine.ApplySpeedLimitAsync(Settings.SpeedLimitKbps);
    }

    public void SaveState()
    {
        lock (_saveLock)
        {
            try
            {
                var list = Downloads.Select(d => new PersistedDownloadState
                {
                    Id = d.Id,
                    Type = d.Type,
                    Name = d.Name,
                    Source = d.Source,
                    SaveDirectory = d.SaveDirectory,
                    FilePath = d.FilePath,
                    Status = d.Status is DownloadStatus.Downloading or DownloadStatus.Seeding or DownloadStatus.Queued
                        ? DownloadStatus.Paused
                        : d.Status,
                    TotalBytes = d.TotalBytes,
                    DownloadedBytes = d.DownloadedBytes,
                    ProgressPercent = d.ProgressPercent,
                    ThreadCount = d.ThreadCount,
                    SupportsResume = d.SupportsResume,
                    CreatedAt = d.CreatedAt,
                    Segments = d.Segments.Select(s => new PersistedSegmentState
                    {
                        Index = s.Index,
                        StartByte = s.StartByte,
                        EndByte = s.EndByte,
                        DownloadedBytes = s.DownloadedBytes
                    }).ToList()
                }).ToList();

                File.WriteAllText(StateFilePath, JsonSerializer.Serialize(list, JsonOptions));
            }
            catch
            {
                // Ignore persistence failures
            }
        }
    }

    private void LoadState()
    {
        if (!File.Exists(StateFilePath)) return;

        try
        {
            var json = File.ReadAllText(StateFilePath);
            var list = JsonSerializer.Deserialize<List<PersistedDownloadState>>(json);
            if (list is null) return;

            foreach (var p in list)
            {
                var item = new DownloadItem
                {
                    Id = p.Id,
                    Type = p.Type,
                    Name = p.Name,
                    Source = p.Source,
                    SaveDirectory = p.SaveDirectory,
                    FilePath = p.FilePath,
                    Status = p.Status,
                    TotalBytes = p.TotalBytes,
                    DownloadedBytes = p.DownloadedBytes,
                    ProgressPercent = p.ProgressPercent,
                    ThreadCount = p.ThreadCount,
                    SupportsResume = p.SupportsResume,
                    CreatedAt = p.CreatedAt
                };

                foreach (var s in p.Segments)
                {
                    item.Segments.Add(new SegmentInfo
                    {
                        Index = s.Index,
                        StartByte = s.StartByte,
                        EndByte = s.EndByte,
                        DownloadedBytes = s.DownloadedBytes,
                        StatusText = p.Status == DownloadStatus.Completed ? "Hoàn thành" : "Tạm dừng"
                    });
                }

                AttachItemWatcher(item);
                Downloads.Add(item);
            }
        }
        catch
        {
            // Ignore corrupted state file
        }
    }

    private static AppSettings LoadSettings()
    {
        if (!File.Exists(SettingsFilePath))
            return new AppSettings();

        try
        {
            var json = File.ReadAllText(SettingsFilePath);
            return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    private void SaveSettings()
    {
        try
        {
            File.WriteAllText(SettingsFilePath, JsonSerializer.Serialize(Settings, JsonOptions));
        }
        catch
        {
            // Ignore
        }
    }

    public async ValueTask DisposeAsync()
    {
        _httpEngine.PauseAll();
        SaveState();
        _httpEngine.Dispose();
        await _torrentEngine.DisposeAsync();
    }
}

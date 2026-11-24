using System.IO;

namespace TurboGet.Models;

public class AppSettings
{
    public string DefaultSaveDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Downloads",
        "TurboGet");

    public string DefaultTorrentSaveDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Downloads",
        "TurboGet",
        "Torrents");

    public bool AskSaveLocationForTorrent { get; set; } = true;

    public int DefaultThreadCount { get; set; } = 8;

    /// <summary>
    /// Global download speed limit in KB/s. 0 means unlimited.
    /// </summary>
    public int SpeedLimitKbps { get; set; } = 0;

    public bool StopSeedingOnComplete { get; set; } = true;

    public bool MinimizeToTrayOnClose { get; set; } = true;

    public bool StartWithWindows { get; set; } = false;

    public bool NotifyOnComplete { get; set; } = true;
}

public class PersistedSegmentState
{
    public int Index { get; set; }
    public long StartByte { get; set; }
    public long EndByte { get; set; }
    public long DownloadedBytes { get; set; }
}

public class PersistedDownloadState
{
    public Guid Id { get; set; }
    public DownloadType Type { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string SaveDirectory { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public DownloadStatus Status { get; set; }
    public long TotalBytes { get; set; }
    public long DownloadedBytes { get; set; }
    public double ProgressPercent { get; set; }
    public int ThreadCount { get; set; } = 8;
    public bool SupportsResume { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public List<PersistedSegmentState> Segments { get; set; } = new();
}

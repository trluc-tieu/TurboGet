using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using TurboGet.Services;

namespace TurboGet.Models;

public class DownloadItem : INotifyPropertyChanged
{
    private string _name = string.Empty;
    private string _filePath = string.Empty;
    private DownloadStatus _status = DownloadStatus.Queued;
    private long _totalBytes;
    private long _downloadedBytes;
    private double _progressPercent;
    private long _downloadSpeedBytesPerSec;
    private long _uploadSpeedBytesPerSec;
    private string _etaText = "-";
    private bool _supportsResume = true;
    private int _seeds;
    private int _peers;
    private string _errorMessage = string.Empty;

    public Guid Id { get; set; } = Guid.NewGuid();
    public DownloadType Type { get; set; }
    public string Source { get; set; } = string.Empty;
    public string SaveDirectory { get; set; } = string.Empty;
    public int ThreadCount { get; set; } = 8;
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public string? Referer { get; set; }
    public string? CookieHeader { get; set; }
    public string? CustomUserAgent { get; set; }

    public string Name
    {
        get => _name;
        set
        {
            if (_name == value) return;
            _name = value;
            OnPropertyChanged();
        }
    }

    public string FilePath
    {
        get => _filePath;
        set
        {
            if (_filePath == value) return;
            _filePath = value;
            OnPropertyChanged();
        }
    }

    public DownloadStatus Status
    {
        get => _status;
        set
        {
            if (_status == value) return;
            _status = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(CanPause));
            OnPropertyChanged(nameof(CanResume));
        }
    }

    public long TotalBytes
    {
        get => _totalBytes;
        set
        {
            if (_totalBytes == value) return;
            _totalBytes = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(FormattedSize));
        }
    }

    public long DownloadedBytes
    {
        get => _downloadedBytes;
        set
        {
            if (_downloadedBytes == value) return;
            _downloadedBytes = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(FormattedSize));
            OnPropertyChanged(nameof(FormattedDownloaded));
        }
    }

    public double ProgressPercent
    {
        get => _progressPercent;
        set
        {
            if (Math.Abs(_progressPercent - value) < 0.01) return;
            _progressPercent = Math.Clamp(value, 0, 100);
            OnPropertyChanged();
            OnPropertyChanged(nameof(FormattedPercent));
        }
    }

    public long DownloadSpeedBytesPerSec
    {
        get => _downloadSpeedBytesPerSec;
        set
        {
            if (_downloadSpeedBytesPerSec == value) return;
            _downloadSpeedBytesPerSec = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(FormattedSpeed));
        }
    }

    public long UploadSpeedBytesPerSec
    {
        get => _uploadSpeedBytesPerSec;
        set
        {
            if (_uploadSpeedBytesPerSec == value) return;
            _uploadSpeedBytesPerSec = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(FormattedUploadSpeed));
        }
    }

    public string EtaText
    {
        get => _etaText;
        set
        {
            if (_etaText == value) return;
            _etaText = value;
            OnPropertyChanged();
        }
    }

    public bool SupportsResume
    {
        get => _supportsResume;
        set
        {
            if (_supportsResume == value) return;
            _supportsResume = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ResumeText));
        }
    }

    public int Seeds
    {
        get => _seeds;
        set
        {
            if (_seeds == value) return;
            _seeds = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ConnectionInfoText));
        }
    }

    public int Peers
    {
        get => _peers;
        set
        {
            if (_peers == value) return;
            _peers = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ConnectionInfoText));
        }
    }

    public string ErrorMessage
    {
        get => _errorMessage;
        set
        {
            if (_errorMessage == value) return;
            _errorMessage = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusText));
        }
    }

    public ObservableCollection<SegmentInfo> Segments { get; } = new();
    public ObservableCollection<TorrentFileDetail> TorrentFiles { get; } = new();

    public bool IsHttp => Type == DownloadType.Http;
    public bool IsTorrent => Type == DownloadType.Torrent;
    public bool CanPause => Status is DownloadStatus.Downloading or DownloadStatus.Seeding or DownloadStatus.Queued;
    public bool CanResume => Status is DownloadStatus.Paused or DownloadStatus.Error;

    public string TypeText => Type == DownloadType.Http ? "HTTP" : "Torrent";

    public string StatusText => Status switch
    {
        DownloadStatus.Queued => "Đang chờ",
        DownloadStatus.Downloading => "Đang tải",
        DownloadStatus.Paused => "Tạm dừng",
        DownloadStatus.Seeding => "Đang Seed",
        DownloadStatus.Completed => "Hoàn thành",
        DownloadStatus.Error => string.IsNullOrWhiteSpace(ErrorMessage) ? "Lỗi" : $"Lỗi: {ErrorMessage}",
        _ => Status.ToString()
    };

    public string FormattedSize => TotalBytes > 0
        ? $"{FormatHelper.FormatBytes(DownloadedBytes)} / {FormatHelper.FormatBytes(TotalBytes)}"
        : (DownloadedBytes > 0 ? FormatHelper.FormatBytes(DownloadedBytes) : "Đang lấy...");

    public string FormattedDownloaded => FormatHelper.FormatBytes(DownloadedBytes);
    public string FormattedPercent => $"{ProgressPercent:F1}%";
    public string FormattedSpeed => DownloadSpeedBytesPerSec > 0 ? FormatHelper.FormatSpeed(DownloadSpeedBytesPerSec) : "-";
    public string FormattedUploadSpeed => UploadSpeedBytesPerSec > 0 ? FormatHelper.FormatSpeed(UploadSpeedBytesPerSec) : "-";
    public string ResumeText => SupportsResume ? "Có hỗ trợ" : "Không hỗ trợ";

    public string ConnectionInfoText => Type == DownloadType.Http
        ? $"{ThreadCount} luồng"
        : $"S:{Seeds} / P:{Peers}";

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

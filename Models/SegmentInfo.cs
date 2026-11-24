using System.ComponentModel;
using System.Runtime.CompilerServices;
using TurboGet.Services;

namespace TurboGet.Models;

public class SegmentInfo : INotifyPropertyChanged
{
    private long _downloadedBytes;
    private long _speedBytesPerSec;
    private string _statusText = "Chờ";

    public int Index { get; set; }
    public long StartByte { get; set; }
    public long EndByte { get; set; }

    public long TotalBytes => EndByte >= StartByte ? (EndByte - StartByte + 1) : 0;

    public long DownloadedBytes
    {
        get => _downloadedBytes;
        set
        {
            if (_downloadedBytes == value) return;
            _downloadedBytes = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ProgressPercent));
            OnPropertyChanged(nameof(FormattedProgress));
        }
    }

    public long SpeedBytesPerSec
    {
        get => _speedBytesPerSec;
        set
        {
            if (_speedBytesPerSec == value) return;
            _speedBytesPerSec = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(FormattedSpeed));
        }
    }

    public string StatusText
    {
        get => _statusText;
        set
        {
            if (_statusText == value) return;
            _statusText = value;
            OnPropertyChanged();
        }
    }

    public double ProgressPercent => TotalBytes > 0
        ? Math.Min(100.0, (double)DownloadedBytes / TotalBytes * 100.0)
        : 0.0;

    public string FormattedProgress => TotalBytes > 0
        ? $"{FormatHelper.FormatBytes(DownloadedBytes)} / {FormatHelper.FormatBytes(TotalBytes)}"
        : FormatHelper.FormatBytes(DownloadedBytes);

    public string FormattedSpeed => SpeedBytesPerSec > 0
        ? FormatHelper.FormatSpeed(SpeedBytesPerSec)
        : "-";

    public string ThreadLabel => $"Luồng #{Index + 1}";

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

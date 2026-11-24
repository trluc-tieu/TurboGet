using System.ComponentModel;
using System.Runtime.CompilerServices;
using TurboGet.Services;

namespace TurboGet.Models;

public class TorrentFileDetail : INotifyPropertyChanged
{
    private double _progressPercent;

    public string FilePath { get; set; } = string.Empty;
    public long SizeBytes { get; set; }

    public double ProgressPercent
    {
        get => _progressPercent;
        set
        {
            if (Math.Abs(_progressPercent - value) < 0.05) return;
            _progressPercent = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(FormattedPercent));
        }
    }

    public string FormattedSize => FormatHelper.FormatBytes(SizeBytes);
    public string FormattedPercent => $"{ProgressPercent:F1}%";

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

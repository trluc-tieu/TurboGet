using System.IO;
using System.Windows;
using Microsoft.Win32;
using TurboGet.Models;

namespace TurboGet.Views;

public partial class SettingsDialog : Window
{
    public AppSettings ResultSettings { get; private set; }

    public SettingsDialog(AppSettings current)
    {
        InitializeComponent();
        ResultSettings = new AppSettings
        {
            DefaultSaveDirectory = current.DefaultSaveDirectory,
            DefaultTorrentSaveDirectory = string.IsNullOrWhiteSpace(current.DefaultTorrentSaveDirectory)
                ? current.DefaultSaveDirectory
                : current.DefaultTorrentSaveDirectory,
            AskSaveLocationForTorrent = current.AskSaveLocationForTorrent,
            DefaultThreadCount = current.DefaultThreadCount,
            SpeedLimitKbps = current.SpeedLimitKbps,
            StopSeedingOnComplete = current.StopSeedingOnComplete,
            MinimizeToTrayOnClose = current.MinimizeToTrayOnClose,
            StartWithWindows = current.StartWithWindows,
            NotifyOnComplete = current.NotifyOnComplete
        };

        TxtDefaultDir.Text = ResultSettings.DefaultSaveDirectory;
        TxtTorrentDir.Text = ResultSettings.DefaultTorrentSaveDirectory;
        ChkAskTorrentLocation.IsChecked = ResultSettings.AskSaveLocationForTorrent;
        SliderThreads.Value = Math.Clamp(ResultSettings.DefaultThreadCount, 1, 32);
        LblThreads.Text = $"{(int)SliderThreads.Value} luồng";
        TxtSpeedLimit.Text = ResultSettings.SpeedLimitKbps.ToString();
        ChkStopSeeding.IsChecked = ResultSettings.StopSeedingOnComplete;
        ChkMinimizeToTray.IsChecked = ResultSettings.MinimizeToTrayOnClose;
        ChkStartWithWindows.IsChecked = ResultSettings.StartWithWindows;
        ChkNotifyOnComplete.IsChecked = ResultSettings.NotifyOnComplete;
    }

    private void SliderThreads_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (LblThreads is not null)
            LblThreads.Text = $"{(int)SliderThreads.Value} luồng";
    }

    private void BtnBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Chọn thư mục lưu file HTTP mặc định",
            InitialDirectory = Directory.Exists(TxtDefaultDir.Text) ? TxtDefaultDir.Text : null
        };
        if (dialog.ShowDialog(this) == true)
        {
            TxtDefaultDir.Text = dialog.FolderName;
        }
    }

    private void BtnBrowseTorrent_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Chọn thư mục lưu Torrent mặc định",
            InitialDirectory = Directory.Exists(TxtTorrentDir.Text) ? TxtTorrentDir.Text : null
        };
        if (dialog.ShowDialog(this) == true)
        {
            TxtTorrentDir.Text = dialog.FolderName;
        }
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(TxtDefaultDir.Text))
        {
            MessageBox.Show(this, "Vui lòng chọn thư mục lưu mặc định.", "Thiếu thông tin",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (string.IsNullOrWhiteSpace(TxtTorrentDir.Text))
        {
            MessageBox.Show(this, "Vui lòng chọn thư mục lưu Torrent mặc định.", "Thiếu thông tin",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!int.TryParse(TxtSpeedLimit.Text.Trim(), out int speedLimit) || speedLimit < 0)
        {
            MessageBox.Show(this, "Giới hạn tốc độ phải là số nguyên >= 0.", "Giá trị không hợp lệ",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        ResultSettings.DefaultSaveDirectory = TxtDefaultDir.Text.Trim();
        ResultSettings.DefaultTorrentSaveDirectory = TxtTorrentDir.Text.Trim();
        ResultSettings.AskSaveLocationForTorrent = ChkAskTorrentLocation.IsChecked == true;
        ResultSettings.DefaultThreadCount = (int)SliderThreads.Value;
        ResultSettings.SpeedLimitKbps = speedLimit;
        ResultSettings.StopSeedingOnComplete = ChkStopSeeding.IsChecked == true;
        ResultSettings.MinimizeToTrayOnClose = ChkMinimizeToTray.IsChecked == true;
        ResultSettings.StartWithWindows = ChkStartWithWindows.IsChecked == true;
        ResultSettings.NotifyOnComplete = ChkNotifyOnComplete.IsChecked == true;
        DialogResult = true;
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}

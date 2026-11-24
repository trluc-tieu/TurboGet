using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using TurboGet.Models;
using TurboGet.Services;
using TurboGet.Views;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace TurboGet;

public partial class MainWindow : Window
{
    private readonly DownloadManagerService _manager;
    private readonly BrowserBridgeServer _bridgeServer;
    private readonly ICollectionView _downloadsView;
    private readonly DispatcherTimer _statusTimer;
    private Forms.NotifyIcon? _trayIcon;
    private IntPtr _trayIconHandle = IntPtr.Zero;
    private string _currentFilter = "All";
    private bool _isClosingCleanly;
    private bool _forceExit;
    private bool _hasShownTrayHint;
    private DownloadItem? _lastCompletedItem;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    public MainWindow()
    {
        InitializeComponent();

        _manager = new DownloadManagerService();
        _downloadsView = CollectionViewSource.GetDefaultView(_manager.Downloads);
        _downloadsView.Filter = FilterDownloadItem;
        GridDownloads.ItemsSource = _downloadsView;

        InitializeTrayIcon();
        FileAssociationHelper.RegisterOpenWithTorrent();

        _manager.DownloadCompleted += item =>
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (_manager.Settings.NotifyOnComplete && _trayIcon is not null)
                {
                    _lastCompletedItem = item;
                    string sizeInfo = item.TotalBytes > 0 ? $" ({FormatHelper.FormatBytes(item.TotalBytes)})" : "";
                    _trayIcon.ShowBalloonTip(
                        4000,
                        "Tải hoàn thành ✅",
                        $"{item.Name}{sizeInfo}\nNhấn vào đây để mở thư mục chứa file.",
                        Forms.ToolTipIcon.Info);
                }
            });
        };

        if (Environment.GetCommandLineArgs().Any(a => string.Equals(a, "--minimized", StringComparison.OrdinalIgnoreCase)))
        {
            Loaded += (_, _) => Hide();
        }

        if (!string.IsNullOrWhiteSpace(App.StartupTorrentArg))
        {
            var initialTorrent = App.StartupTorrentArg;
            Loaded += async (_, _) =>
            {
                var added = await _manager.AddTorrentDownloadAsync(initialTorrent);
                _downloadsView.Refresh();
                GridDownloads.SelectedItem = added;
            };
        }

        _bridgeServer = new BrowserBridgeServer(
            _manager,
            onDownloadAdded: added =>
            {
                _downloadsView.Refresh();
                GridDownloads.SelectedItem = added;

                if (!IsVisible && _trayIcon is not null)
                {
                    _trayIcon.ShowBalloonTip(
                        2500,
                        "TurboGet đang tải ⚡",
                        added.Name,
                        Forms.ToolTipIcon.Info);
                }
            },
            onFocusRequested: RestoreFromTray);
        _bridgeServer.Start();
        TxtBridgeStatus.Text = _bridgeServer.StatusText;

        _statusTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _statusTimer.Tick += (_, _) => UpdateStatusBarAndInspector();
        _statusTimer.Start();

        UpdateToolbarButtons();
        UpdateStatusBarAndInspector();
    }

    private void InitializeTrayIcon()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("⚡ Mở TurboGet", null, (_, _) => Dispatcher.Invoke(RestoreFromTray));
        menu.Items.Add("➕ Thêm tải xuống mới...", null, async (_, _) =>
        {
            await Dispatcher.InvokeAsync(async () =>
            {
                RestoreFromTray();
                await ShowAddDialogAsync(startInTorrentMode: false);
            });
        });
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("❌ Thoát hoàn toàn", null, (_, _) => Dispatcher.Invoke(ExitApplication));

        _trayIcon = new Forms.NotifyIcon
        {
            Icon = CreateTrayIcon(),
            Text = "TurboGet — Sẵn sàng",
            Visible = true,
            ContextMenuStrip = menu
        };

        _trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(RestoreFromTray);
        _trayIcon.BalloonTipClicked += (_, _) => Dispatcher.Invoke(() =>
        {
            if (_lastCompletedItem is { } done)
            {
                if (!string.IsNullOrWhiteSpace(done.FilePath) && File.Exists(done.FilePath))
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{done.FilePath}\"") { UseShellExecute = true });
                else if (Directory.Exists(done.SaveDirectory))
                    OpenPathInExplorer(done.SaveDirectory);
                _lastCompletedItem = null;
            }
            else
            {
                RestoreFromTray();
            }
        });
    }

    private Drawing.Icon CreateTrayIcon()
    {
        using var bmp = new Drawing.Bitmap(32, 32);
        using (var g = Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var bgBrush = new Drawing.Drawing2D.LinearGradientBrush(
                new Drawing.Point(0, 0),
                new Drawing.Point(32, 32),
                Drawing.Color.FromArgb(255, 14, 165, 233),
                Drawing.Color.FromArgb(255, 29, 78, 216));
            g.FillEllipse(bgBrush, 1, 1, 30, 30);

            var pts = new[]
            {
                new Drawing.PointF(18f, 4.5f),
                new Drawing.PointF(9f, 17.5f),
                new Drawing.PointF(16f, 17.5f),
                new Drawing.PointF(13.5f, 27.5f),
                new Drawing.PointF(23.5f, 14f),
                new Drawing.PointF(16.5f, 14f)
            };
            g.FillPolygon(Drawing.Brushes.White, pts);
        }

        _trayIconHandle = bmp.GetHicon();
        return Drawing.Icon.FromHandle(_trayIconHandle);
    }

    private void RestoreFromTray()
    {
        if (!IsVisible)
            Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    private void ExitApplication()
    {
        _forceExit = true;
        Close();
    }

    private DownloadItem? SelectedItem => GridDownloads.SelectedItem as DownloadItem;

    private bool FilterDownloadItem(object obj)
    {
        if (obj is not DownloadItem item) return false;
        return _currentFilter switch
        {
            "Downloading" => item.Status is DownloadStatus.Downloading or DownloadStatus.Queued or DownloadStatus.Seeding,
            "Completed" => item.Status == DownloadStatus.Completed,
            "Paused" => item.Status is DownloadStatus.Paused or DownloadStatus.Error,
            "Http" => item.Type == DownloadType.Http,
            "Torrent" => item.Type == DownloadType.Torrent,
            _ => true
        };
    }

    private void NavCategory_Checked(object sender, RoutedEventArgs e)
    {
        if (_downloadsView is null) return;
        if (sender is RadioButton rb && rb.Tag is string tag)
        {
            _currentFilter = tag;
            _downloadsView.Refresh();
        }
    }

    private async void BtnAddUrl_Click(object sender, RoutedEventArgs e)
    {
        await ShowAddDialogAsync(startInTorrentMode: false);
    }

    private async void BtnAddTorrent_Click(object sender, RoutedEventArgs e)
    {
        await ShowAddDialogAsync(startInTorrentMode: true);
    }

    private async Task ShowAddDialogAsync(bool startInTorrentMode, string? prefillSource = null)
    {
        var dialog = new AddDownloadDialog(
            _manager.Settings.DefaultSaveDirectory,
            _manager.Settings.DefaultThreadCount,
            startInTorrentMode,
            prefillSource)
        {
            Owner = this
        };

        if (dialog.ShowDialog() == true)
        {
            DownloadItem added;
            if (dialog.SelectedType == DownloadType.Http)
            {
                added = await _manager.AddHttpDownloadAsync(
                    dialog.SourceText,
                    dialog.SaveDirectory,
                    dialog.ThreadCount);
            }
            else
            {
                added = await _manager.AddTorrentDownloadAsync(
                    dialog.SourceText,
                    dialog.SaveDirectory);
            }

            _downloadsView.Refresh();
            GridDownloads.SelectedItem = added;
        }
    }

    private async void BtnResume_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedItem is null) return;
        await _manager.StartOrResumeAsync(SelectedItem);
        UpdateToolbarButtons();
    }

    private async void BtnPause_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedItem is null) return;
        await _manager.PauseAsync(SelectedItem);
        UpdateToolbarButtons();
    }

    private async void BtnDelete_Click(object sender, RoutedEventArgs e)
    {
        await RemoveSelectedAsync(deleteFiles: false);
    }

    private async void MenuDeleteWithFiles_Click(object sender, RoutedEventArgs e)
    {
        await RemoveSelectedAsync(deleteFiles: true);
    }

    private async Task RemoveSelectedAsync(bool deleteFiles)
    {
        var item = SelectedItem;
        if (item is null) return;

        var msg = deleteFiles
            ? $"Bạn có chắc muốn xóa '{item.Name}' VÀ xóa file đã tải trên đĩa?"
            : $"Bạn có chắc muốn xóa '{item.Name}' khỏi danh sách?";

        if (MessageBox.Show(this, msg, "Xác nhận xóa", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        await _manager.RemoveAsync(item, deleteFiles);
        UpdateInspector();
        UpdateToolbarButtons();
    }

    private void BtnOpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var item = SelectedItem;
        if (item is null)
        {
            OpenPathInExplorer(_manager.Settings.DefaultSaveDirectory);
            return;
        }

        if (!string.IsNullOrWhiteSpace(item.FilePath) && File.Exists(item.FilePath))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{item.FilePath}\"") { UseShellExecute = true });
        }
        else if (!string.IsNullOrWhiteSpace(item.FilePath) && Directory.Exists(item.FilePath))
        {
            OpenPathInExplorer(item.FilePath);
        }
        else if (Directory.Exists(item.SaveDirectory))
        {
            OpenPathInExplorer(item.SaveDirectory);
        }
    }

    private void MenuOpenFile_Click(object sender, RoutedEventArgs e)
    {
        OpenSelectedItem();
    }

    private void MenuCopySource_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedItem is null) return;
        try
        {
            Clipboard.SetText(SelectedItem.Source);
        }
        catch { }
    }

    private void GridDownloads_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        OpenSelectedItem();
    }

    private void OpenSelectedItem()
    {
        var item = SelectedItem;
        if (item is null) return;

        if (!string.IsNullOrWhiteSpace(item.FilePath) && (File.Exists(item.FilePath) || Directory.Exists(item.FilePath)))
        {
            try
            {
                Process.Start(new ProcessStartInfo(item.FilePath) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Không thể mở file: {ex.Message}", "Lỗi", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private static void OpenPathInExplorer(string path)
    {
        if (!Directory.Exists(path)) return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    }

    private async void BtnSettings_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsDialog(_manager.Settings) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            await _manager.UpdateSettingsAsync(dialog.ResultSettings);
            UpdateStatusBarAndInspector();
        }
    }

    private void GridDownloads_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateInspector();
        UpdateToolbarButtons();
    }

    private void UpdateToolbarButtons()
    {
        var item = SelectedItem;
        BtnResume.IsEnabled = item is not null && item.CanResume;
        BtnPause.IsEnabled = item is not null && item.CanPause;
        BtnDelete.IsEnabled = item is not null;
    }

    private void UpdateInspector()
    {
        var item = SelectedItem;
        if (item is null)
        {
            TxtNoSelection.Visibility = Visibility.Visible;
            PanelHttpDetail.Visibility = Visibility.Collapsed;
            PanelTorrentDetail.Visibility = Visibility.Collapsed;
            return;
        }

        TxtNoSelection.Visibility = Visibility.Collapsed;

        if (item.Type == DownloadType.Http)
        {
            PanelHttpDetail.Visibility = Visibility.Visible;
            PanelTorrentDetail.Visibility = Visibility.Collapsed;

            TxtHttpTitle.Text = $"{item.Name}  —  {item.Source}";
            TxtHttpMeta.Text = $"Luồng: {item.ThreadCount} | Resume: {item.ResumeText}";
            ListSegmentBars.ItemsSource = item.Segments;
            GridSegments.ItemsSource = item.Segments;
        }
        else
        {
            PanelHttpDetail.Visibility = Visibility.Collapsed;
            PanelTorrentDetail.Visibility = Visibility.Visible;

            TxtTorrentTitle.Text = item.Name;
            TxtTorrentPeers.Text = $"Seeds: {item.Seeds} | Peers: {item.Peers} | Upload: {item.FormattedUploadSpeed}";
            TxtTorrentPath.Text = $"Lưu tại: {(string.IsNullOrWhiteSpace(item.FilePath) ? item.SaveDirectory : item.FilePath)}";
            GridTorrentFiles.ItemsSource = item.TorrentFiles;
        }
    }

    private void UpdateStatusBarAndInspector()
    {
        int total = _manager.Downloads.Count;
        int active = _manager.Downloads.Count(d => d.Status is DownloadStatus.Downloading or DownloadStatus.Seeding);
        long totalDown = _manager.Downloads.Sum(d => d.DownloadSpeedBytesPerSec);
        long totalUp = _manager.Downloads.Sum(d => d.UploadSpeedBytesPerSec);

        TxtStatusLeft.Text = $"Tổng: {total} mục | Đang hoạt động: {active}";
        TxtStatusLimit.Text = _manager.Settings.SpeedLimitKbps > 0
            ? $"Giới hạn: {_manager.Settings.SpeedLimitKbps} KB/s"
            : "Giới hạn: Không giới hạn";
        TxtStatusDownSpeed.Text = $"⬇ {(totalDown > 0 ? FormatHelper.FormatSpeed(totalDown) : "0 B/s")}";
        TxtStatusUpSpeed.Text = $"⬆ {(totalUp > 0 ? FormatHelper.FormatSpeed(totalUp) : "0 B/s")}";

        if (_trayIcon is not null)
        {
            string tip = active > 0
                ? $"TurboGet — Đang tải: {active} ({FormatHelper.FormatSpeed(totalDown)})"
                : "TurboGet — Sẵn sàng bắt link";
            _trayIcon.Text = tip.Length > 63 ? tip[..63] : tip;
        }

        // Refresh dynamic inspector text
        if (SelectedItem is { } item)
        {
            if (item.Type == DownloadType.Http)
            {
                TxtHttpTitle.Text = $"{item.Name}  —  {item.Source}";
                TxtHttpMeta.Text = $"Luồng: {item.ThreadCount} | Resume: {item.ResumeText}";
            }
            else
            {
                TxtTorrentTitle.Text = item.Name;
                TxtTorrentPeers.Text = $"Seeds: {item.Seeds} | Peers: {item.Peers} | Upload: {item.FormattedUploadSpeed}";
                TxtTorrentPath.Text = $"Lưu tại: {(string.IsNullOrWhiteSpace(item.FilePath) ? item.SaveDirectory : item.FilePath)}";
            }
            UpdateToolbarButtons();
        }
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var files = (string[]?)e.Data.GetData(DataFormats.FileDrop);
            if (files?.Any(f => f.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase)) == true)
            {
                e.Effects = DragDropEffects.Copy;
                e.Handled = true;
                return;
            }
        }
        e.Effects = DragDropEffects.None;
        e.Handled = true;
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        var files = (string[]?)e.Data.GetData(DataFormats.FileDrop);
        if (files is null) return;

        foreach (var file in files.Where(f => f.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase)))
        {
            await ShowAddDialogAsync(startInTorrentMode: true, prefillSource: file);
        }
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!_forceExit && _manager.Settings.MinimizeToTrayOnClose)
        {
            e.Cancel = true;
            Hide();
            if (!_hasShownTrayHint && _trayIcon is not null)
            {
                _hasShownTrayHint = true;
                _trayIcon.ShowBalloonTip(
                    2500,
                    "TurboGet đang chạy ngầm",
                    "Ứng dụng đã thu nhỏ về khay hệ thống để tiếp tục tải và bắt link từ trình duyệt.",
                    Forms.ToolTipIcon.Info);
            }
            return;
        }

        if (_isClosingCleanly) return;

        e.Cancel = true;
        _isClosingCleanly = true;
        _statusTimer.Stop();

        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }

        if (_trayIconHandle != IntPtr.Zero)
        {
            DestroyIcon(_trayIconHandle);
            _trayIconHandle = IntPtr.Zero;
        }

        try
        {
            _bridgeServer.Dispose();
            await _manager.DisposeAsync();
        }
        catch { }

        Application.Current.Shutdown();
    }
}

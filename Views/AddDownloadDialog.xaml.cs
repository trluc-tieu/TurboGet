using System.IO;
using System.Windows;
using Microsoft.Win32;
using TurboGet.Models;

namespace TurboGet.Views;

public partial class AddDownloadDialog : Window
{
    public DownloadType SelectedType => RbTorrent.IsChecked == true ? DownloadType.Torrent : DownloadType.Http;
    public string SourceText => TxtSource.Text.Trim();
    public string SaveDirectory => TxtSaveDir.Text.Trim();
    public int ThreadCount => (int)SliderThreads.Value;

    public AddDownloadDialog(string defaultSaveDir, int defaultThreads, bool startInTorrentMode = false, string? prefillSource = null)
    {
        InitializeComponent();

        TxtSaveDir.Text = defaultSaveDir;
        SliderThreads.Value = Math.Clamp(defaultThreads, 1, 32);
        LblThreadValue.Text = $"{(int)SliderThreads.Value} luồng";

        if (startInTorrentMode)
        {
            RbTorrent.IsChecked = true;
        }

        if (!string.IsNullOrWhiteSpace(prefillSource))
        {
            TxtSource.Text = prefillSource;
            if (prefillSource.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase) ||
                prefillSource.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase))
            {
                RbTorrent.IsChecked = true;
            }
        }
        else
        {
            TryAutoFillFromClipboard(startInTorrentMode);
        }

        UpdateModeUi();
        TxtSource.Focus();
    }

    private void TryAutoFillFromClipboard(bool torrentMode)
    {
        try
        {
            if (!Clipboard.ContainsText()) return;
            var clip = Clipboard.GetText().Trim();
            if (string.IsNullOrWhiteSpace(clip) || clip.Contains('\n')) return;

            if (clip.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
            {
                RbTorrent.IsChecked = true;
                TxtSource.Text = clip;
            }
            else if (!torrentMode &&
                     (clip.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                      clip.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
            {
                RbHttp.IsChecked = true;
                TxtSource.Text = clip;
            }
        }
        catch
        {
            // Ignore clipboard errors
        }
    }

    private void ModeChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        UpdateModeUi();
    }

    private void UpdateModeUi()
    {
        if (RbTorrent.IsChecked == true)
        {
            LblSource.Text = "Link Magnet (magnet:?xt=...) hoặc đường dẫn file .torrent:";
            BtnBrowseTorrent.Visibility = Visibility.Visible;
            PanelThreads.Visibility = Visibility.Collapsed;
        }
        else
        {
            LblSource.Text = "Đường dẫn tải xuống (HTTP / HTTPS URL):";
            BtnBrowseTorrent.Visibility = Visibility.Collapsed;
            PanelThreads.Visibility = Visibility.Visible;
        }
    }

    private void SliderThreads_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (LblThreadValue is not null)
            LblThreadValue.Text = $"{(int)SliderThreads.Value} luồng";
    }

    private void BtnBrowseTorrent_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Chọn file Torrent",
            Filter = "Torrent Files (*.torrent)|*.torrent|All Files (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) == true)
        {
            TxtSource.Text = dialog.FileName;
        }
    }

    private void BtnBrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Chọn thư mục lưu file",
            InitialDirectory = Directory.Exists(TxtSaveDir.Text) ? TxtSaveDir.Text : null
        };
        if (dialog.ShowDialog(this) == true)
        {
            TxtSaveDir.Text = dialog.FolderName;
        }
    }

    private void BtnStart_Click(object sender, RoutedEventArgs e)
    {
        var src = SourceText;
        if (string.IsNullOrWhiteSpace(src))
        {
            MessageBox.Show(this, "Vui lòng nhập đường dẫn tải xuống hoặc chọn file .torrent.", "Thiếu thông tin",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (SelectedType == DownloadType.Http)
        {
            if (!Uri.TryCreate(src, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                MessageBox.Show(this, "URL không hợp lệ. Vui lòng nhập link bắt đầu bằng http:// hoặc https://.", "URL không hợp lệ",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }
        else
        {
            if (!src.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase) && !File.Exists(src))
            {
                MessageBox.Show(this, "Vui lòng nhập link magnet hợp lệ hoặc chọn file .torrent tồn tại trên máy.", "Torrent không hợp lệ",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }

        if (string.IsNullOrWhiteSpace(SaveDirectory))
        {
            MessageBox.Show(this, "Vui lòng chọn thư mục lưu file.", "Thiếu thư mục",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        DialogResult = true;
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}

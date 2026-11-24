using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace TurboGet.Services;

public static class FileAssociationHelper
{
    private const int SHCNE_ASSOCCHANGED = 0x08000000;
    private const int SHCNF_IDLIST = 0x0000;

    [DllImport("shell32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern void SHChangeNotify(int wEventId, int uFlags, IntPtr dwItem1, IntPtr dwItem2);

    /// <summary>
    /// Registers TurboGet in Windows "Open with" (Mở bằng) list for .torrent files (per-user, no admin needed).
    /// </summary>
    public static void RegisterOpenWithTorrent()
    {
        try
        {
            var exePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(exePath)) return;

            var command = $"\"{exePath}\" \"%1\"";

            // 1. Register under HKCU\Software\Classes\Applications\TurboGet.exe
            using (var appKey = Registry.CurrentUser.CreateSubKey(@"Software\Classes\Applications\TurboGet.exe"))
            {
                appKey?.SetValue("FriendlyAppName", "TurboGet");
            }
            using (var appCmd = Registry.CurrentUser.CreateSubKey(@"Software\Classes\Applications\TurboGet.exe\shell\open\command"))
            {
                appCmd?.SetValue("", command);
            }
            using (var supported = Registry.CurrentUser.CreateSubKey(@"Software\Classes\Applications\TurboGet.exe\SupportedTypes"))
            {
                supported?.SetValue(".torrent", "");
            }

            // 2. Register ProgID TurboGet.Torrent
            using (var progId = Registry.CurrentUser.CreateSubKey(@"Software\Classes\TurboGet.Torrent"))
            {
                progId?.SetValue("", "TurboGet Torrent File");
            }
            using (var iconKey = Registry.CurrentUser.CreateSubKey(@"Software\Classes\TurboGet.Torrent\DefaultIcon"))
            {
                iconKey?.SetValue("", $"\"{exePath}\",0");
            }
            using (var progCmd = Registry.CurrentUser.CreateSubKey(@"Software\Classes\TurboGet.Torrent\shell\open\command"))
            {
                progCmd?.SetValue("", command);
            }

            // 3. Add TurboGet.Torrent to .torrent\OpenWithProgids so it appears in "Mở bằng"
            using (var openWith = Registry.CurrentUser.CreateSubKey(@"Software\Classes\.torrent\OpenWithProgids"))
            {
                openWith?.SetValue("TurboGet.Torrent", "");
            }

            // 4. Also add to .torrent\OpenWithList
            using (var openWithList = Registry.CurrentUser.CreateSubKey(@"Software\Classes\.torrent\OpenWithList\TurboGet.exe"))
            {
                openWithList?.SetValue("", "");
            }

            SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
        }
        catch
        {
            // Ignore registry errors
        }
    }
}

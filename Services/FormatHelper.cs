using System.IO;
using System.Text.RegularExpressions;

namespace TurboGet.Services;

public static class FormatHelper
{
    private static readonly string[] SizeUnits = ["B", "KB", "MB", "GB", "TB"];

    public static string FormatBytes(long bytes)
    {
        if (bytes < 0) return "0 B";
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < SizeUnits.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{value:F0} {SizeUnits[unit]}" : $"{value:F2} {SizeUnits[unit]}";
    }

    public static string FormatSpeed(long bytesPerSec)
    {
        if (bytesPerSec <= 0) return "-";
        return $"{FormatBytes(bytesPerSec)}/s";
    }

    public static string FormatEta(long remainingBytes, long speedBytesPerSec)
    {
        if (remainingBytes <= 0 || speedBytesPerSec <= 0) return "-";
        var seconds = remainingBytes / speedBytesPerSec;
        if (seconds > 86400 * 30) return "∞";
        var ts = TimeSpan.FromSeconds(seconds);
        if (ts.Days > 0) return $"{ts.Days}d {ts.Hours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}";
        if (ts.Hours > 0) return $"{ts.Hours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}";
        return $"{ts.Minutes:D2}:{ts.Seconds:D2}";
    }

    public static string SanitizeFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "download.bin";
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        cleaned = Regex.Replace(cleaned, @"\s+", " ");
        return string.IsNullOrWhiteSpace(cleaned) ? "download.bin" : cleaned;
    }

    public static string GetUniqueFilePath(string directory, string fileName)
    {
        Directory.CreateDirectory(directory);
        var sanitized = SanitizeFileName(fileName);
        var baseName = Path.GetFileNameWithoutExtension(sanitized);
        var ext = Path.GetExtension(sanitized);
        var candidate = Path.Combine(directory, sanitized);
        int counter = 1;
        while (File.Exists(candidate) || File.Exists(candidate + ".tgpart"))
        {
            candidate = Path.Combine(directory, $"{baseName} ({counter}){ext}");
            counter++;
        }
        return candidate;
    }
}

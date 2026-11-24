using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TurboGet.Services;

public sealed record UpdateCheckResult(
    bool Success,
    bool IsUpdateAvailable,
    Version CurrentVersion,
    Version? LatestVersion,
    string? ReleaseName,
    string? ReleaseNotes,
    string? DownloadUrl,
    string? AssetName,
    string? ReleasePageUrl,
    string? ErrorMessage);

public static class UpdateCheckerService
{
    public const string RepoOwner = "trluc-tieu";
    public const string RepoName = "TurboGet";
    private const string LatestReleaseApi = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest";

    public static Version GetCurrentVersion()
    {
        var v = Assembly.GetExecutingAssembly().GetName().Version;
        if (v is null) return new Version(1, 0, 0);
        return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
    }

    public static string GetCurrentVersionText()
    {
        var v = GetCurrentVersion();
        return $"v{v.Major}.{v.Minor}.{v.Build}";
    }

    public static async Task<UpdateCheckResult> CheckForUpdateAsync(CancellationToken ct = default)
    {
        var current = GetCurrentVersion();

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd($"TurboGet/{current}");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

            using var resp = await client.GetAsync(LatestReleaseApi, ct);
            if (!resp.IsSuccessStatusCode)
            {
                return new UpdateCheckResult(
                    false, false, current, null, null, null, null, null, null,
                    $"Không thể kiểm tra cập nhật (HTTP {(int)resp.StatusCode}).");
            }

            var json = await resp.Content.ReadAsStringAsync(ct);
            var release = JsonSerializer.Deserialize<GitHubReleaseDto>(json);
            if (release is null || string.IsNullOrWhiteSpace(release.TagName))
            {
                return new UpdateCheckResult(
                    false, false, current, null, null, null, null, null, null,
                    "Không đọc được thông tin phiên bản từ GitHub.");
            }

            var latest = ParseVersion(release.TagName);
            if (latest is null)
            {
                return new UpdateCheckResult(
                    false, false, current, null, release.Name, release.Body, null, null, release.HtmlUrl,
                    $"Định dạng tag phiên bản không hợp lệ: {release.TagName}");
            }

            var preferredAsset = release.Assets?
                .FirstOrDefault(a =>
                    !string.IsNullOrWhiteSpace(a.BrowserDownloadUrl) &&
                    (a.Name?.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) == true ||
                     a.Name?.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) == true))
                ?? release.Assets?.FirstOrDefault(a => !string.IsNullOrWhiteSpace(a.BrowserDownloadUrl));

            bool isNewer = latest > current;

            return new UpdateCheckResult(
                Success: true,
                IsUpdateAvailable: isNewer,
                CurrentVersion: current,
                LatestVersion: latest,
                ReleaseName: string.IsNullOrWhiteSpace(release.Name) ? release.TagName : release.Name,
                ReleaseNotes: release.Body,
                DownloadUrl: preferredAsset?.BrowserDownloadUrl,
                AssetName: preferredAsset?.Name,
                ReleasePageUrl: release.HtmlUrl,
                ErrorMessage: null);
        }
        catch (Exception ex)
        {
            return new UpdateCheckResult(
                false, false, current, null, null, null, null, null, null,
                $"Lỗi kết nối GitHub: {ex.Message}");
        }
    }

    private static Version? ParseVersion(string tag)
    {
        var cleaned = tag.Trim().TrimStart('v', 'V');
        var dashIdx = cleaned.IndexOf('-');
        if (dashIdx >= 0) cleaned = cleaned[..dashIdx];

        if (!Version.TryParse(cleaned, out var parsed))
            return null;

        return new Version(parsed.Major, parsed.Minor, Math.Max(0, parsed.Build));
    }

    private sealed class GitHubReleaseDto
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("body")]
        public string? Body { get; set; }

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; set; }

        [JsonPropertyName("assets")]
        public List<GitHubAssetDto>? Assets { get; set; }
    }

    private sealed class GitHubAssetDto
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("browser_download_url")]
        public string? BrowserDownloadUrl { get; set; }
    }
}

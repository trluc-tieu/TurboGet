namespace TurboGet.Models;

public enum DownloadType
{
    Http,
    Torrent
}

public enum DownloadStatus
{
    Queued,
    Downloading,
    Paused,
    Seeding,
    Completed,
    Error
}

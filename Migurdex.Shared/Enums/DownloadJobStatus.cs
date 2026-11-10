namespace Migurdex.Shared.Enums;

public enum DownloadJobStatus
{
    Queued,
    Resolving,
    Downloading,
    Subtitle,
    Finalizing,
    Paused,
    Completed,
    Failed,
    Cancelled
}

public static class DownloadJobStatusExtensions
{
    public static bool IsInFlight(this DownloadJobStatus status)
    {
        return status is DownloadJobStatus.Resolving
                         or DownloadJobStatus.Downloading
                         or DownloadJobStatus.Subtitle
                         or DownloadJobStatus.Finalizing;
    }

    public static bool IsActiveOrPending(this DownloadJobStatus status)
    {
        return status is DownloadJobStatus.Queued || status.IsInFlight();
    }

    public static bool IsTerminal(this DownloadJobStatus status)
    {
        return status is DownloadJobStatus.Completed
                         or DownloadJobStatus.Failed
                         or DownloadJobStatus.Cancelled;
    }
}

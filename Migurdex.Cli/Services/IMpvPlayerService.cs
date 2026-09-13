using Migurdex.Cli.Configuration;
using Migurdex.Shared.Models;

namespace Migurdex.Cli.Services;

public interface IMpvPlayerService
{
    Task<SyncOutcome> PlayAsync(
        string                      videoUrl,
        WatchHistoryEntry           historyEntry,
        Dictionary<string, string>? headers           = null,
        List<Subtitle>?             subtitles         = null,
        CancellationToken           cancellationToken = default);
}

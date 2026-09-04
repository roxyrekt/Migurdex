using Migurdex.Shared.Models;

namespace Migurdex.Shared.Interfaces;

public interface IExtractor
{
    string Name { get; }

    bool CanExtract(string url);

    Task<List<VideoSource>> ExtractAsync(string url,
        IDictionary<string, string>?            headers           = null,
        CancellationToken                       cancellationToken = default);
}

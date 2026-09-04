using Migurdex.Shared.Models;

namespace Migurdex.Shared.Interfaces;

public interface IExtractorManager
{
    IReadOnlyList<IExtractor> Extractors { get; }

    void RegisterExtractor(IExtractor extractor);

    Task<List<VideoSource>> ExtractAsync(string url,
        IDictionary<string, string>?            headers           = null,
        CancellationToken                       cancellationToken = default);

    bool CanExtract(string url);
}

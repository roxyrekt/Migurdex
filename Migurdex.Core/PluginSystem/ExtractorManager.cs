using Microsoft.Extensions.Logging;
using Migurdex.Shared.Diagnostics;
using Migurdex.Shared.Enums;
using Migurdex.Shared.Interfaces;
using Migurdex.Shared.Models;
using System.Collections.Concurrent;

namespace Migurdex.Core.PluginSystem;

public class ExtractorManager : IExtractorManager
{
    private const int MetadataFillTimeoutSeconds = 6;

    private readonly ConcurrentDictionary<string, IExtractor> _builtInExtractors =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly ILogger<ExtractorManager> _logger;
    private readonly IMp4MetadataReader        _metadataReader;
    private readonly PluginLoader              _pluginLoader;

    public ExtractorManager(ILogger<ExtractorManager> logger,
        IMp4MetadataReader                            metadataReader,
        PluginLoader                                  pluginLoader)
    {
        _logger         = logger;
        _metadataReader = metadataReader;
        _pluginLoader   = pluginLoader;
    }

    public IReadOnlyList<IExtractor> Extractors
    {
        get
        {
            var pluginExtractors = _pluginLoader.Extractors;
            if (pluginExtractors.Count == 0)
            {
                return _builtInExtractors.Values.ToList();
            }

            var list = new List<IExtractor>(_builtInExtractors.Count + pluginExtractors.Count);
            list.AddRange(_builtInExtractors.Values);
            list.AddRange(pluginExtractors);
            return list;
        }
    }

    public void RegisterExtractor(IExtractor extractor)
    {
        if (_builtInExtractors.TryAdd(extractor.Name, extractor))
        {
            _logger.LogInformation("built-in extractor registered: {ExtractorName}", extractor.Name);
        }
    }

    public bool CanExtract(string url)
    {
        url = NormalizeUrl(url);

        foreach (var extractor in _builtInExtractors.Values)
        {
            if (extractor.CanExtract(url))
            {
                return true;
            }
        }

        foreach (var extractor in _pluginLoader.Extractors)
        {
            if (extractor.CanExtract(url))
            {
                return true;
            }
        }

        return false;
    }

    public async Task<List<VideoSource>> ExtractAsync(string url,
        IDictionary<string, string>?                         headers           = null,
        CancellationToken                                    cancellationToken = default)
    {
        var outcome = await ExtractDetailedAsync(url, headers, cancellationToken);

        return outcome.Sources;
    }

    public async Task<ExtractionOutcome> ExtractDetailedAsync(string url,
        IDictionary<string, string>?                             headers           = null,
        CancellationToken                                        cancellationToken = default)
    {
        url = NormalizeUrl(url);

        var matchedExtractors = new List<IExtractor>();
        foreach (var extractor in _builtInExtractors.Values)
        {
            if (extractor.CanExtract(url))
            {
                matchedExtractors.Add(extractor);
            }
        }

        foreach (var extractor in _pluginLoader.Extractors)
        {
            if (extractor.CanExtract(url))
            {
                matchedExtractors.Add(extractor);
            }
        }

        if (matchedExtractors.Count == 0)
        {
            _logger.LogWarning("no extractor found for URL: {Url}", url);

            return ExtractionOutcome.Empty;
        }

        var sources  = new List<VideoSource>();
        var failures = new List<ExtractionFailure>();
        foreach (var extractor in matchedExtractors)
        {
            try
            {
                _logger.LogDebug("extracting URL using {ExtractorName}: {Url}", extractor.Name, url);
                using var scope = ExtractionCapture.Begin();
                var result = await extractor.ExtractAsync(url, headers, cancellationToken);

                foreach (var source in result)
                {
                    source.Hoster ??= extractor.Name;
                }

                if (result.Count == 0)
                {
                    _logger.LogWarning("{ExtractorName} returned no sources for {Url}",
                                       extractor.Name,
                                       url);
                }

                sources.AddRange(result);
                failures.AddRange(StampFailures(extractor.Name, scope.Failures));
            }
            catch (ExtractionException ex)
            {
                if (ex.Kind == UpstreamErrorKind.UpstreamChanged)
                {
                    _logger.LogError("{ExtractorName} upstream structure changed for {Url}: {Detail}",
                                     extractor.Name,
                                     url,
                                     ex.Message);
                }
                else
                {
                    _logger.LogWarning("{ExtractorName} upstream failure ({Kind}) for {Url}: {Detail}",
                                       extractor.Name,
                                       ex.Kind,
                                       url,
                                       ex.Message);
                }

                failures.Add(new ExtractionFailure(extractor.Name, ex.Kind, ex.Message));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                                 "error occurred during extraction with {ExtractorName} for {Url}",
                                 extractor.Name,
                                 url);
                failures.Add(new ExtractionFailure(extractor.Name, UpstreamErrorKind.Unknown, "iç extractor hatası"));
            }
        }

        if (sources.Count == 0 && matchedExtractors.Count > 0)
        {
            _logger.LogWarning("all {Count} extractor(s) returned empty for {Url}",
                               matchedExtractors.Count,
                               url);
        }

        List<ExtractionFailure> probeFailures;
        using (var probeScope = ExtractionCapture.Begin())
        {
            await FillMissingMetadataAsync(sources, cancellationToken);
            probeFailures = StampFailures(sources.FirstOrDefault()?.Hoster ?? "probe",
                                          probeScope.Failures);
        }

        failures.AddRange(probeFailures);

        return new ExtractionOutcome(sources, DedupeFailures(failures));
    }

    private static List<ExtractionFailure> StampFailures(string extractor, IEnumerable<ExtractionFailure> failures)
    {
        return failures.Select(f => string.IsNullOrEmpty(f.Extractor) ? f with { Extractor = extractor } : f)
                       .ToList();
    }

    private static List<ExtractionFailure> DedupeFailures(List<ExtractionFailure> failures)
    {
        return failures.GroupBy(f => (f.Kind, f.Detail))
                       .Select(g => g.FirstOrDefault(f => !string.IsNullOrEmpty(f.Extractor)) ?? g.First())
                       .ToList();
    }

    public Task EnrichSourcesAsync(List<VideoSource> sources,
        CancellationToken                           cancellationToken = default)
    {
        return FillMissingMetadataAsync(sources, cancellationToken, dropUnprobable: false);
    }

    private async Task FillMissingMetadataAsync(List<VideoSource> sources,
        CancellationToken                                         cancellationToken,
        bool                                                    dropUnprobable = true)
    {
        var pending = sources.Where(s => s.Type == VideoType.Mp4
                                         && !string.IsNullOrWhiteSpace(s.Url)
                                         && (s.Bitrate is null
                                             || s.Width is null
                                             || s.Height is null
                                             || s.SizeBytes is null
                                             || s.DurationSeconds is null
                                             || s.VideoCodec is null
                                             || s.AudioCodec is null
                                             || string.IsNullOrWhiteSpace(s.Quality)
                                             || s.Quality.Equals("Auto", StringComparison.OrdinalIgnoreCase)))
                             .ToList();
        if (pending.Count == 0)
        {
            return;
        }

        var groups = pending.GroupBy(s => s.Url, StringComparer.OrdinalIgnoreCase).ToList();

        await Task.WhenAll(groups.Select(async group =>
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(MetadataFillTimeoutSeconds));

            try
            {
                var first = group.First();
                var requestHeaders = first.Headers is { Count: > 0 }
                                         ? first.Headers
                                         : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var metadata =
                    await _metadataReader.GetVideoMetadataAsync(first.Url, requestHeaders, cts.Token);

                foreach (var source in group)
                {
                    if ((string.IsNullOrWhiteSpace(source.Quality)
                         || source.Quality.Equals("Auto", StringComparison.OrdinalIgnoreCase))
                        && !string.IsNullOrWhiteSpace(metadata.Quality))
                    {
                        source.Quality = metadata.Quality;
                    }

                    source.Bitrate         ??= metadata.Bitrate;
                    source.Width           ??= metadata.Width;
                    source.Height          ??= metadata.Height;
                    source.VideoCodec      ??= metadata.VideoCodec;
                    source.AudioCodec      ??= metadata.AudioCodec;
                    source.SizeBytes       ??= metadata.SizeBytes;
                    source.DurationSeconds ??= metadata.DurationSeconds;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "bitrate fill failed for {Url}", group.Key);
            }
        }));

        if (!dropUnprobable)
        {
            return;
        }

        var dropped = sources.RemoveAll(s => s.Type == VideoType.Mp4
                                             && (string.IsNullOrWhiteSpace(s.Quality)
                                                 || s.Quality.Equals("Auto", StringComparison.OrdinalIgnoreCase))
                                             && s.Bitrate is null
                                             && s.DurationSeconds is null);
        if (dropped > 0)
        {
            _logger.LogWarning("dropped {Count} unprobable sources (first: {Url})", dropped, groups.First().Key);
        }
    }

    private static string NormalizeUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return url;
        }

        url = url.Trim();
        if (url.StartsWith("//"))
        {
            return "https:" + url;
        }

        return url;
    }
}

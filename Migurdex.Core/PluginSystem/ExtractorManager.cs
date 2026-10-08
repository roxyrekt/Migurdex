using Microsoft.Extensions.Logging;
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
        IMp4MetadataReader                                metadataReader,
        PluginLoader                                      pluginLoader)
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

            return [];
        }

        var sources = new List<VideoSource>();
        foreach (var extractor in matchedExtractors)
        {
            try
            {
                _logger.LogDebug("extracting URL using {ExtractorName}: {Url}", extractor.Name, url);
                var result = await extractor.ExtractAsync(url, headers, cancellationToken);

                foreach (var source in result)
                {
                    source.Hoster ??= extractor.Name;
                }

                sources.AddRange(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                                 "error occurred during extraction with {ExtractorName} for {Url}",
                                 extractor.Name,
                                 url);
            }
        }

        await FillMissingMetadataAsync(sources, headers, cancellationToken);

        return sources;
    }

    private async Task FillMissingMetadataAsync(List<VideoSource> sources,
        IDictionary<string, string>?                                    headers,
        CancellationToken                                               cancellationToken)
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

        var fallbackHeaders = headers as Dictionary<string, string>
                              ?? headers?.ToDictionary(x => x.Key, x => x.Value)
                              ?? new Dictionary<string, string>();

        var groups = pending.GroupBy(s => s.Url, StringComparer.OrdinalIgnoreCase).ToList();

        await Task.WhenAll(groups.Select(async group =>
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(MetadataFillTimeoutSeconds));

            try
            {
                var first          = group.First();
                var requestHeaders = first.Headers is { Count: > 0 } ? first.Headers : fallbackHeaders;
                var metadata       =
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

        var dropped = sources.RemoveAll(s => s.Type == VideoType.Mp4
                                             && (string.IsNullOrWhiteSpace(s.Quality)
                                                 || s.Quality.Equals("Auto", StringComparison.OrdinalIgnoreCase))
                                             && s.Bitrate is null
                                             && s.DurationSeconds is null);
        if (dropped > 0)
        {
            _logger.LogDebug("dropped {Count} unprobable sources", dropped);
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

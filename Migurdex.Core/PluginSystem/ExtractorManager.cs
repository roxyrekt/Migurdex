using Microsoft.Extensions.Logging;
using Migurdex.Shared.Interfaces;
using Migurdex.Shared.Models;
using System.Collections.Concurrent;

namespace Migurdex.Core.PluginSystem;

public class ExtractorManager : IExtractorManager
{
    private readonly ConcurrentDictionary<string, IExtractor> _builtInExtractors =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly ILogger<ExtractorManager> _logger;
    private readonly PluginLoader              _pluginLoader;

    public ExtractorManager(ILogger<ExtractorManager> logger, PluginLoader pluginLoader)
    {
        _logger       = logger;
        _pluginLoader = pluginLoader;
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

        return sources;
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

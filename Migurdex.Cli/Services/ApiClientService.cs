using Migurdex.Cli.Configuration;
using Migurdex.Cli.Utils;
using Migurdex.Shared.Models;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Migurdex.Cli.Services;

public class ApiClientService : IApiClientService
{
    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IConfigurationService _configService;
    private readonly HttpClient            _httpClient;

    public ApiClientService(HttpClient httpClient, IConfigurationService configService)
    {
        _httpClient    = httpClient;
        _configService = configService;

        var configuredUrl = (_configService.Config.ApiBaseUrl ?? string.Empty).Trim().TrimEnd('/') + "/";
        if (!Uri.TryCreate(configuredUrl, UriKind.Absolute, out var baseAddress)
            || (baseAddress.Scheme != Uri.UriSchemeHttp && baseAddress.Scheme != Uri.UriSchemeHttps))
        {
            Console.Error.WriteLine(
                $"Uyarı: geçersiz API adresi ('{_configService.Config.ApiBaseUrl}'), varsayılan kullanılıyor.");
            baseAddress = new Uri(new CliConfig().ApiBaseUrl.TrimEnd('/') + "/");
        }

        _httpClient.BaseAddress = baseAddress;
    }

    private static readonly Lock _apiLogLock = new();

    private static void AppendApiLog(string path, string? line)
    {
        if (string.IsNullOrEmpty(line))
        {
            return;
        }

        try
        {
            lock (_apiLogLock)
            {
                File.AppendAllText(path, line + Environment.NewLine);
            }
        }
        catch
        {
            // ignored
        }
    }

    public async Task<bool> IsApiOnlineAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromMilliseconds(500));
            var response = await _httpClient.GetAsync("health", cts.Token);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> TryStartApiDaemonAsync(CancellationToken cancellationToken = default)
    {
        if (await IsApiOnlineAsync(cancellationToken))
        {
            return true;
        }

        var cliDir            = AppContext.BaseDirectory;
        var isWindows         = OperatingSystem.IsWindows();
        var apiExecutableName = isWindows ? "Migurdex.Api.exe" : "Migurdex.Api";

        var possiblePaths = new[]
        {
            Path.Combine(cliDir, "api", apiExecutableName),
            Path.Combine(cliDir, "api", "Migurdex.Api.dll"),
            Path.Combine(cliDir, "Migurdex.Api.dll"),
            Path.Combine(cliDir, "..", "Migurdex.Api", "Migurdex.Api.dll"),
            Path.Combine(cliDir,
                         "..",
                         "..",
                         "..",
                         "..",
                         "Migurdex.Api",
                         "bin",
                         "Debug",
                         "net10.0",
                         "Migurdex.Api.dll"),
            Path.Combine(cliDir,
                         "..",
                         "..",
                         "..",
                         "..",
                         "Migurdex.Api",
                         "bin",
                         "Release",
                         "net10.0",
                         "Migurdex.Api.dll"),
            Path.Combine(cliDir, apiExecutableName)
        };

        string? apiPath = null;
        foreach (var path in possiblePaths)
        {
            var fullPath = Path.GetFullPath(path);
            if (File.Exists(fullPath))
            {
                apiPath = fullPath;
                break;
            }
        }

        if (apiPath == null)
        {
            return false;
        }

        string? apiLogPath = null;
        try
        {
            var logDir = Path.Combine(_configService.ConfigDirectory, "logs");
            Directory.CreateDirectory(logDir);
            apiLogPath = Path.Combine(logDir, "api.log");

            if (new FileInfo(apiLogPath).Exists && new FileInfo(apiLogPath).Length > 5 * 1024 * 1024)
            {
                File.Delete(apiLogPath);
            }
        }
        catch
        {
            apiLogPath = null;
        }

        try
        {
            var isDll = apiPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
            var psi = new ProcessStartInfo
            {
                FileName               = isDll ? "dotnet" : apiPath,
                Arguments              = isDll ? $"\"{apiPath}\"" : "",
                UseShellExecute        = false,
                CreateNoWindow         = true,
                WindowStyle            = ProcessWindowStyle.Hidden,
                WorkingDirectory       = Path.GetDirectoryName(apiPath) ?? cliDir,
                RedirectStandardOutput = apiLogPath != null,
                RedirectStandardError  = apiLogPath != null
            };

            psi.EnvironmentVariables["ASPNETCORE_URLS"] = _configService.Config.ApiBaseUrl;

            var process = Process.Start(psi);
            if (process != null)
            {
                ChildProcessTracker.Track(process);
                if (apiLogPath != null)
                {
                    var logFile = apiLogPath;
                    process.OutputDataReceived += (_, e) => AppendApiLog(logFile, e.Data);
                    process.ErrorDataReceived  += (_, e) => AppendApiLog(logFile, e.Data);
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                }
            }

            for (var i = 0; i < 40; i++)
            {
                if (await IsApiOnlineAsync(cancellationToken))
                {
                    return true;
                }

                await Task.Delay(250, cancellationToken);
            }
        }
        catch
        {
            // ignored
        }

        return false;
    }

    public async Task<ApiResult<IReadOnlyList<ProviderInfo>>> GetProvidersAsync(CancellationToken cancellationToken =
        default)
    {
        try
        {
            var providers =
                await _httpClient.GetFromJsonAsync<List<ProviderInfo>>("api/v1/providers",
                                                                       _jsonOpts,
                                                                       cancellationToken);
            return ApiResult<IReadOnlyList<ProviderInfo>>.Ok(providers ?? []);
        }
        catch
        {
            return ApiResult<IReadOnlyList<ProviderInfo>>.Fail([], "Sağlayıcı listesi alınamadı.");
        }
    }

    public async Task<ApiResult<IReadOnlyList<SearchResult>>> SearchAnimeAsync(string query,
        string?                                                                       provider          = null,
        CancellationToken                                                             cancellationToken = default)
    {
        try
        {
            var url = $"api/v1/anime/search?q={Uri.EscapeDataString(query)}";
            if (!string.IsNullOrEmpty(provider))
            {
                url += $"&provider={Uri.EscapeDataString(provider)}";
            }

            var results =
                await _httpClient.GetFromJsonAsync<List<SearchResultWrapper>>(url, _jsonOpts, cancellationToken);

            var disabled = _configService.Config.DisabledProviders;
            var failedProviders = results?.Where(r => r.Data is null && r.Error is not null)
                                         .Select(r => r.Provider)
                                         .ToList()
                                  ?? [];

            var items = results?.SelectMany(r => r.Data ?? [])
                               .Where(r => !disabled.Contains(r.ProviderName, StringComparer.OrdinalIgnoreCase))
                               .ToList()
                        ?? [];

            if (failedProviders.Count > 0 && items.Count == 0)
            {
                return ApiResult<IReadOnlyList<SearchResult>>.Fail(items,
                                                                   $"Arama başarısız ({string.Join(", ", failedProviders)}).");
            }

            return ApiResult<IReadOnlyList<SearchResult>>.Ok(items);
        }
        catch
        {
            return ApiResult<IReadOnlyList<SearchResult>>.Fail([], "Arama yapılamadı.");
        }
    }

    public async IAsyncEnumerable<StreamedSearchResult> SearchAnimeStreamAsync(string query,
        string?                                                                       provider          = null,
        [EnumeratorCancellation] CancellationToken                                    cancellationToken = default,
        StreamScanStats?                                                              stats             = null)
    {
        var url = $"api/v1/anime/search?q={Uri.EscapeDataString(query)}&stream=true";
        if (!string.IsNullOrEmpty(provider))
        {
            url += $"&provider={Uri.EscapeDataString(provider)}";
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var response =
            await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var apiError = await ReadApiErrorAsync(response, cancellationToken);
            if (stats is not null)
            {
                Interlocked.Increment(ref stats.Errors);
            }

            yield return new StreamedSearchResult
            {
                Provider = provider ?? string.Empty,
                Status   = "error",
                Error    = apiError
            };

            yield break;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var       reader = new StreamReader(stream);

        string? currentEvent = null;

        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line == null)
            {
                break;
            }

            if (string.IsNullOrEmpty(line))
            {
                continue;
            }

            if (line.StartsWith("event:", StringComparison.Ordinal))
            {
                currentEvent = line["event:".Length..].Trim();
                continue;
            }

            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                var data = line["data:".Length..].Trim();
                var evt  = currentEvent;
                currentEvent = null;

                if (string.Equals(evt, "done", StringComparison.OrdinalIgnoreCase))
                {
                    yield break;
                }

                if (string.Equals(evt, "providerError", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(evt, "error", StringComparison.OrdinalIgnoreCase))
                {
                    ProviderStreamError? err = null;
                    try
                    {
                        err = JsonSerializer.Deserialize<ProviderStreamError>(data, _jsonOpts);
                    }
                    catch
                    {
                        // ignored
                    }

                    if (err != null && !string.IsNullOrEmpty(err.Provider))
                    {
                        if (_configService.Config.DisabledProviders.Contains(
                                err.Provider,
                                StringComparer.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        if (stats is not null)
                        {
                            Interlocked.Increment(ref stats.Errors);
                        }

                        yield return new StreamedSearchResult
                        {
                            Provider = err.Provider,
                            Status   = "error",
                            Error    = err.Error
                        };
                    }

                    continue;
                }

                StreamedSearchResult? result = null;
                try
                {
                    result = JsonSerializer.Deserialize<StreamedSearchResult>(data, _jsonOpts);
                }
                catch
                {
                    // ignored
                }

                if (result != null)
                {
                    if (_configService.Config.DisabledProviders.Contains(
                            result.Provider,
                            StringComparer.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (stats is not null)
                    {
                        Interlocked.Increment(ref stats.Received);
                    }

                    yield return result;
                }
            }
        }
    }

    public async Task<ApiResult<AnimeDetails?>> GetAnimeDetailsAsync(string provider,
        string                                                              animeId,
        CancellationToken                                                   cancellationToken = default)
    {
        try
        {
            var url     = $"api/v1/anime/{Uri.EscapeDataString(provider)}/{Uri.EscapeDataString(animeId)}";
            var details = await _httpClient.GetFromJsonAsync<AnimeDetails>(url, _jsonOpts, cancellationToken);
            return details is not null
                       ? ApiResult<AnimeDetails?>.Ok(details)
                       : ApiResult<AnimeDetails?>.Fail(null, "Anime detayları alınamadı.");
        }
        catch
        {
            return ApiResult<AnimeDetails?>.Fail(null, "Anime detayları alınamadı.");
        }
    }

    public async Task<ApiResult<IReadOnlyList<string>>> GetEpisodeGroupsAsync(string provider,
        string                                                                       episodeId,
        CancellationToken                                                            cancellationToken = default)
    {
        try
        {
            var url =
                $"api/v1/anime/{Uri.EscapeDataString(provider)}/groups?episodeId={Uri.EscapeDataString(episodeId)}";
            var groups = await _httpClient.GetFromJsonAsync<List<string>>(url, _jsonOpts, cancellationToken);
            return ApiResult<IReadOnlyList<string>>.Ok(groups ?? []);
        }
        catch
        {
            return ApiResult<IReadOnlyList<string>>.Fail([], "Fansub grupları alınamadı.");
        }
    }

    public async Task<ApiResult<IReadOnlyList<VideoSource>>> GetVideoSourcesAsync(string provider,
        string                                                                           episodeId,
        string?                                                                          group             = null,
        CancellationToken                                                                cancellationToken = default)
    {
        try
        {
            var url =
                $"api/v1/anime/{Uri.EscapeDataString(provider)}/sources?episodeId={Uri.EscapeDataString(episodeId)}";
            if (!string.IsNullOrEmpty(group))
            {
                url += $"&group={Uri.EscapeDataString(group)}";
            }

            var sources = await _httpClient.GetFromJsonAsync<List<VideoSource>>(url, _jsonOpts, cancellationToken);
            return ApiResult<IReadOnlyList<VideoSource>>.Ok(sources ?? []);
        }
        catch
        {
            return ApiResult<IReadOnlyList<VideoSource>>.Fail([], "Video kaynakları alınamadı.");
        }
    }

    public async IAsyncEnumerable<VideoSource> GetVideoSourcesStreamAsync(string provider,
        string                                                                   episodeId,
        string?                                                                  group             = null,
        [EnumeratorCancellation] CancellationToken                               cancellationToken = default,
        StreamScanStats?                                                         stats             = null)
    {
        var url =
            $"api/v1/anime/{Uri.EscapeDataString(provider)}/sources?episodeId={Uri.EscapeDataString(episodeId)}&stream=true";
        if (!string.IsNullOrEmpty(group))
        {
            url += $"&group={Uri.EscapeDataString(group)}";
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var response =
            await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            if (stats is not null)
            {
                Interlocked.Increment(ref stats.Errors);
            }

            yield break;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var       reader = new StreamReader(stream);

        string? currentEvent = null;

        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line == null)
            {
                break;
            }

            if (string.IsNullOrEmpty(line))
            {
                continue;
            }

            if (line.StartsWith("event:", StringComparison.Ordinal))
            {
                currentEvent = line["event:".Length..].Trim();
                continue;
            }

            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                var data = line["data:".Length..].Trim();
                var evt  = currentEvent;
                currentEvent = null;

                if (string.Equals(evt, "done", StringComparison.OrdinalIgnoreCase))
                {
                    yield break;
                }

                if (string.Equals(evt, "providerError", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(evt, "error", StringComparison.OrdinalIgnoreCase))
                {
                    if (stats is not null)
                    {
                        Interlocked.Increment(ref stats.Errors);
                    }

                    continue;
                }

                VideoSource? source = null;
                try
                {
                    source = JsonSerializer.Deserialize<VideoSource>(data, _jsonOpts);
                }
                catch
                {
                    // ignored
                }

                if (source != null)
                {
                    if (stats is not null)
                    {
                        Interlocked.Increment(ref stats.Received);
                    }

                    yield return source;
                }
            }
        }
    }

    public async Task<ApiResult<IReadOnlyList<string>>> GetExtractorsAsync(CancellationToken cancellationToken =
        default)
    {
        try
        {
            var results =
                await _httpClient.GetFromJsonAsync<List<ExtractorResponse>>(
                    "api/v1/extractors",
                    _jsonOpts,
                    cancellationToken);
            return ApiResult<IReadOnlyList<string>>.Ok(results?.Select(r => r.Name).ToList() ?? []);
        }
        catch
        {
            return ApiResult<IReadOnlyList<string>>.Fail([], "Extractor listesi alınamadı.");
        }
    }

    private static async Task<string> ReadApiErrorAsync(HttpResponseMessage response,
        CancellationToken                                                   cancellationToken)
    {
        try
        {
            var       body = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc  = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var err)
                && err.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(err.GetString()))
            {
                return err.GetString()!;
            }
        }
        catch
        {
            // ignored
        }

        return $"HTTP {(int) response.StatusCode}";
    }

    private class ExtractorResponse
    {
        public string Name { get; } = string.Empty;
    }

    private class SearchResultWrapper
    {
        public string              Provider { get; } = string.Empty;
        public List<SearchResult>? Data     { get; set; }
        public string?             Error    { get; set; }
    }

    private class ProviderStreamError
    {
        public string  Provider { get; set; } = string.Empty;
        public string? Scope    { get; set; }
        public string? Error    { get; set; }
    }
}

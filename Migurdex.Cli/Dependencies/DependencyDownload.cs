using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace Migurdex.Cli.Dependencies;

public sealed record GitHubAssetInfo(string Name, string DownloadUrl, long Size, string? Sha256);

public sealed record GitHubReleaseInfo(string Tag, IReadOnlyList<GitHubAssetInfo> Assets);

public static class DependencyDownload
{
    public const int DefaultParallelism = 4;

    private const long MinChunkBytes = 1024 * 1024;

    public static HttpClient CreateHttpClient()
    {
        return new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout         = TimeSpan.FromSeconds(15),
            AllowAutoRedirect      = true
        })
        {
            Timeout = TimeSpan.FromMinutes(10)
        };
    }

    public static async Task<string> GetStringAsync(
        HttpClient        httpClient,
        string            url,
        CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task DownloadFileAsync(
        HttpClient         httpClient,
        string             url,
        string             destination,
        string?            expectedSha256,
        IProgress<string>? progress,
        CancellationToken  cancellationToken,
        int                maxParallelism = DefaultParallelism)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);

        var fullDestination = Path.GetFullPath(destination);
        Directory.CreateDirectory(Path.GetDirectoryName(fullDestination)!);

        var tempPath    = fullDestination + ".part-" + Guid.NewGuid().ToString("N");
        var parallelism = Math.Clamp(maxParallelism, 1, 8);

        try
        {
            progress?.Report($"İndiriliyor: {url}");

            var probe = await ProbeAsync(httpClient, url, cancellationToken).ConfigureAwait(false);
            var chunks = probe.SupportsRanges && parallelism > 1
                             ? ComputeChunks(probe.Length, parallelism)
                             : [];

            if (chunks.Count > 1)
            {
                try
                {
                    await DownloadParallelAsync(httpClient,
                                                url,
                                                tempPath,
                                                chunks,
                                                progress,
                                                cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is InvalidDataException or HttpRequestException
                                           && !cancellationToken.IsCancellationRequested)
                {
                    progress?.Report("Parçalı indirme başarısız; tek akışa dönülüyor...");
                    TryDelete(tempPath);

                    await DownloadSingleAsync(httpClient, url, tempPath, progress, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            else
            {
                await DownloadSingleAsync(httpClient, url, tempPath, progress, cancellationToken)
                    .ConfigureAwait(false);
            }

            var info = new FileInfo(tempPath);
            if (!info.Exists || info.Length == 0)
            {
                throw new IOException("İndirilen dosya boş.");
            }

            var actual   = await ComputeSha256Async(tempPath, cancellationToken).ConfigureAwait(false);
            var expected = NormalizeSha256(expectedSha256);

            if (expected is not null && !actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"İndirilen dosyanın SHA-256 özeti eşleşmiyor (beklenen {expected}, alınan {actual}).");
            }

            if (expected is null)
            {
                progress?.Report("Uyarı: bu indirme için SHA-256 özeti bulunamadı; doğrulama atlandı.");
            }

            File.Move(tempPath, fullDestination, true);
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    internal static IReadOnlyList<(long Start, long End)> ComputeChunks(long length, int maxParallelism)
    {
        if (length <= 0 || maxParallelism <= 1)
        {
            return [];
        }

        var bySize    = (int) Math.Min(maxParallelism, Math.Max(1, length / MinChunkBytes));
        var count     = Math.Max(1, bySize);
        var chunkSize = (length + count - 1) / count;

        var chunks = new List<(long Start, long End)>(count);
        for (var i = 0; i < count; i++)
        {
            var start = i * chunkSize;
            if (start >= length)
            {
                break;
            }

            var end = Math.Min(start + chunkSize - 1, length - 1);
            chunks.Add((start, end));
        }

        return chunks;
    }

    private sealed record ProbeResult(bool SupportsRanges, long Length);

    private static async Task<ProbeResult> ProbeAsync(
        HttpClient        httpClient,
        string            url,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Range = new RangeHeaderValue(0, 0);

            using var response = await httpClient.SendAsync(request,
                                                            HttpCompletionOption.ResponseHeadersRead,
                                                            cancellationToken)
                                                 .ConfigureAwait(false);

            var total = response.Content.Headers.ContentRange?.Length;

            if (response.StatusCode == HttpStatusCode.PartialContent && total is > 0)
            {
                return new ProbeResult(true, total.Value);
            }

            var length = response.Content.Headers.ContentLength ?? -1;

            return new ProbeResult(false, length);
        }
        catch (HttpRequestException)
        {
            return new ProbeResult(false, -1);
        }
    }

    private static async Task DownloadSingleAsync(
        HttpClient         httpClient,
        string             url,
        string             tempPath,
        IProgress<string>? progress,
        CancellationToken  cancellationToken)
    {
        using var response = await httpClient.GetAsync(url,
                                                       HttpCompletionOption.ResponseHeadersRead,
                                                       cancellationToken)
                                             .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var  total = response.Content.Headers.ContentLength ?? -1;
        long read  = 0;

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken)
                                               .ConfigureAwait(false);
        await using var target = new FileStream(tempPath,
                                                FileMode.Create,
                                                FileAccess.Write,
                                                FileShare.None,
                                                81920,
                                                true);

        var buffer = new byte[81920];
        int n;
        while ((n = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, n), cancellationToken).ConfigureAwait(false);
            read += n;

            if (total > 0)
            {
                progress?.Report($"İndiriliyor: %{(int) (read * 100 / total)}");
            }
        }
    }

    private static async Task DownloadParallelAsync(
        HttpClient                            httpClient,
        string                                url,
        string                                tempPath,
        IReadOnlyList<(long Start, long End)> chunks,
        IProgress<string>?                    progress,
        CancellationToken                     cancellationToken)
    {
        var partDirectory = tempPath + ".chunks";
        Directory.CreateDirectory(partDirectory);

        var length      = chunks[^1].End + 1;
        var parts       = new string[chunks.Count];
        var counters    = new long[chunks.Count];
        var lastPercent = new int[1];
        lastPercent[0] = -1;

        try
        {
            var tasks = new Task[chunks.Count];
            for (var i = 0; i < chunks.Count; i++)
            {
                var index = i;
                var (start, end) = chunks[index];
                var partPath = Path.Combine(partDirectory, index.ToString("D3"));

                parts[index] = partPath;
                tasks[index] = DownloadChunkAsync(httpClient,
                                                  url,
                                                  start,
                                                  end,
                                                  partPath,
                                                  delta =>
                                                  {
                                                      Interlocked.Add(ref counters[index], delta);

                                                      var downloaded = 0L;
                                                      for (var c = 0; c < counters.Length; c++)
                                                      {
                                                          downloaded += Interlocked.Read(ref counters[c]);
                                                      }

                                                      var percent = (int) (downloaded * 100 / length);
                                                      if (percent == Volatile.Read(ref lastPercent[0]))
                                                      {
                                                          return;
                                                      }

                                                      Volatile.Write(ref lastPercent[0], percent);
                                                      progress?.Report($"İndiriliyor: %{percent}");
                                                  },
                                                  cancellationToken);
            }

            await Task.WhenAll(tasks).ConfigureAwait(false);

            await using var output = new FileStream(tempPath,
                                                    FileMode.Create,
                                                    FileAccess.Write,
                                                    FileShare.None,
                                                    81920,
                                                    true);

            foreach (var part in parts)
            {
                await using var input = new FileStream(part,
                                                       FileMode.Open,
                                                       FileAccess.Read,
                                                       FileShare.Read,
                                                       81920,
                                                       true);

                await input.CopyToAsync(output, 81920, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            TryDeleteDirectory(partDirectory);
        }
    }

    private static async Task DownloadChunkAsync(
        HttpClient        httpClient,
        string            url,
        long              start,
        long              end,
        string            partPath,
        Action<int>       onProgress,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new RangeHeaderValue(start, end);

        using var response = await httpClient.SendAsync(request,
                                                        HttpCompletionOption.ResponseHeadersRead,
                                                        cancellationToken)
                                             .ConfigureAwait(false);

        if (response.StatusCode != HttpStatusCode.PartialContent)
        {
            throw new InvalidDataException("Sunucu parçalı indirmeyi yanıtlamadı.");
        }

        response.EnsureSuccessStatusCode();

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken)
                                               .ConfigureAwait(false);
        await using var target = new FileStream(partPath,
                                                FileMode.Create,
                                                FileAccess.Write,
                                                FileShare.None,
                                                81920,
                                                true);

        var expected = end - start + 1;
        var written  = 0L;

        var buffer = new byte[81920];
        int n;
        while ((n = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, n), cancellationToken).ConfigureAwait(false);
            written += n;
            onProgress(n);
        }

        if (written != expected)
        {
            throw new InvalidDataException(
                $"Parça {start}-{end} eksik indi ({written}/{expected} bayt).");
        }
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        using var       hash   = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        var buffer = new byte[81920];
        int n;
        while ((n = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            hash.AppendData(buffer, 0, n);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    public static string? ParseSha256Manifest(string? manifest, string fileName)
    {
        if (string.IsNullOrWhiteSpace(manifest) || string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        foreach (var rawLine in manifest.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var parts = line.Split((char[]?) null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                continue;
            }

            var hash = parts[0];
            if (!IsSha256(hash))
            {
                continue;
            }

            var name = parts[^1].TrimStart('*');
            if (name.Equals(fileName, StringComparison.OrdinalIgnoreCase)
                || Path.GetFileName(name).Equals(fileName, StringComparison.OrdinalIgnoreCase))
            {
                return hash.ToLowerInvariant();
            }
        }

        return null;
    }

    public static string? ExtractSha256(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        foreach (var token in text.Split((char[]?) null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (IsSha256(token))
            {
                return token.ToLowerInvariant();
            }
        }

        return null;
    }

    public static string? NormalizeSha256(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (trimmed.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed["sha256:".Length..];
        }

        return IsSha256(trimmed) ? trimmed.ToLowerInvariant() : null;
    }

    public static bool IsSha256(string? value)
    {
        return value is { Length: 64 } && value.All(char.IsAsciiHexDigit);
    }

    public static async Task<GitHubReleaseInfo?> GetLatestGitHubReleaseAsync(
        HttpClient        httpClient,
        string            repository,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repository);

        using var request = new HttpRequestMessage(HttpMethod.Get,
                                                   $"https://api.github.com/repos/{repository}/releases/latest");
        request.Headers.TryAddWithoutValidation("User-Agent", "migurdex");
        request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");

        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
                                               .ConfigureAwait(false);
        var root = document.RootElement;
        if (!root.TryGetProperty("tag_name", out var tagElement))
        {
            return null;
        }

        var assets = new List<GitHubAssetInfo>();
        if (root.TryGetProperty("assets", out var assetsElement)
            && assetsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assetsElement.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
                var url = asset.TryGetProperty("browser_download_url", out var urlElement)
                              ? urlElement.GetString()
                              : null;
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(url))
                {
                    continue;
                }

                var size = asset.TryGetProperty("size", out var sizeElement) && sizeElement.TryGetInt64(out var parsed)
                               ? parsed
                               : 0;

                var sha = asset.TryGetProperty("digest", out var digestElement)
                              ? NormalizeSha256(digestElement.GetString())
                              : null;

                assets.Add(new GitHubAssetInfo(name, url, size, sha));
            }
        }

        return new GitHubReleaseInfo(tagElement.GetString() ?? string.Empty, assets);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
        catch
        {
        }
    }
}

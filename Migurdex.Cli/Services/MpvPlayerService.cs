using Migurdex.Cli.Utils;
using Migurdex.Core.Services;
using Migurdex.Shared.Models;
using Spectre.Console;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Migurdex.Cli.Services;

public class MpvPlayerService : IMpvPlayerService
{
    private readonly IConfigurationService _configService;
    private readonly IHistoryService       _historyService;
    private readonly HttpClient            _httpClient;
    private readonly IDiscordRpcService    _rpcService;
    private readonly WatchSyncService      _syncService;

    public MpvPlayerService(
        IConfigurationService configService,
        IHistoryService       historyService,
        IDiscordRpcService    rpcService,
        WatchSyncService      syncService,
        HttpClient            httpClient)
    {
        _configService  = configService;
        _historyService = historyService;
        _rpcService     = rpcService;
        _syncService    = syncService;
        _httpClient     = httpClient;
    }

    public async Task<SyncOutcome> PlayAsync(
        string                      videoUrl,
        WatchHistoryEntry           historyEntry,
        Dictionary<string, string>? headers           = null,
        List<Subtitle>?             subtitles         = null,
        CancellationToken           cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(videoUrl))
        {
            throw new InvalidOperationException("Video URL boş.");
        }

        var playerExe = _configService.Config.PreferredPlayer;
        var playerPath = FindPlayerExecutable(playerExe)
                         ?? throw new InvalidOperationException(
                             $"Medya oynatıcı bulunamadı: '{playerExe}'. MPV kurun (https://mpv.io/).");

        var isWindows = OperatingSystem.IsWindows();
        var ipcPath = isWindows
                          ? "migurdex-mpv-pipe"
                          : $"/tmp/migurdex-mpv-{Guid.NewGuid():N}.sock";

        var argList = new List<string>
        {
            videoUrl,
            isWindows ? $"--input-ipc-server=\\\\.\\pipe\\{ipcPath}" : $"--input-ipc-server={ipcPath}"
        };

        if (headers is { Count: > 0 })
        {
            var headerList = new List<string>();
            foreach (var kvp in headers)
            {
                headerList.Add($"{kvp.Key}: {kvp.Value}");
            }

            argList.Add($"--http-header-fields={string.Join(",", headerList)}");
        }

        var     tempFiles  = new List<string>();
        string? tempSubDir = null;
        if (subtitles is { Count: > 0 })
        {
            var usableSubs = subtitles
                             .Select((sub, idx) => (sub, idx))
                             .Where(t => !string.IsNullOrWhiteSpace(t.sub.Url))
                             .ToList();

            if (usableSubs.Count > 0)
            {
                tempSubDir = Path.Combine(Path.GetTempPath(), $"migurdex-subs-{Guid.NewGuid():N}");
                Directory.CreateDirectory(tempSubDir);

                var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var planned = usableSubs.Select(t =>
                                        {
                                            var extension = ResolveSubtitleExtension(t.sub);
                                            var baseName  = BuildSubtitleBaseName(t.sub, t.idx);
                                            var fileName  = GetUniqueFileName(usedNames, baseName, extension);
                                            return (t.sub, t.idx, filePath: Path.Combine(tempSubDir, fileName));
                                        })
                                        .ToList();

                var tasks = planned.Select(p =>
                                               p.sub.Url.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                                                   ? TryWriteDataUriSubtitleAsync(
                                                       p.sub.Url,
                                                       p.filePath,
                                                       cancellationToken)
                                                   : TryDownloadSubtitleAsync(p.sub, p.filePath, cancellationToken))
                                   .ToArray();

                var results = await Task.WhenAll(tasks);
                for (var k = 0; k < planned.Count; k++)
                {
                    var localPath = results[k];
                    if (localPath is not null)
                    {
                        tempFiles.Add(localPath);
                        argList.Add($"--sub-file={localPath}");
                    }
                    else if (!planned[k].sub.Url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                    {
                        argList.Add($"--sub-file={planned[k].sub.Url}");
                    }
                }
            }
        }

        if (historyEntry.LastPositionSeconds > 0)
        {
            argList.Add($"--start={(int) historyEntry.LastPositionSeconds}");
        }

        var animeTitle = historyEntry.AnimeTitle.Trim();
        var mediaTitle = animeTitle;
        if (historyEntry.EpisodeNumber > 0)
        {
            var season = Math.Max(1, historyEntry.Season);
            var episode = historyEntry.EpisodeNumber % 1 == 0
                              ? ((int) historyEntry.EpisodeNumber).ToString()
                              : historyEntry.EpisodeNumber.ToString("0.#",
                                                                    CultureInfo.InvariantCulture);
            mediaTitle = $"{animeTitle} S{season}E{episode}";
        }

        if (!string.IsNullOrWhiteSpace(historyEntry.EpisodeTitle))
        {
            mediaTitle = string.IsNullOrWhiteSpace(mediaTitle)
                             ? historyEntry.EpisodeTitle.Trim()
                             : $"{mediaTitle} - {historyEntry.EpisodeTitle.Trim()}";
        }

        if (!string.IsNullOrWhiteSpace(mediaTitle))
        {
            argList.Add($"--title={mediaTitle}");
            argList.Add($"--force-media-title={mediaTitle}");
        }

        if (!_configService.Config.ShowPlayerLogs)
        {
            argList.Add("--terminal=no");
            argList.Add("--really-quiet");
        }

        var psi = new ProcessStartInfo
        {
            FileName               = playerPath,
            UseShellExecute        = false,
            CreateNoWindow         = true,
            RedirectStandardOutput = false,
            RedirectStandardError  = false
        };

        foreach (var arg in argList)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Medya oynatıcı başlatılamadı.");
        ChildProcessTracker.Track(process);

        if (!_configService.Config.ShowPlayerLogs)
        {
            AnsiConsole.Clear();
            AnsiConsole.MarkupLine(
                $"[bold cyan]Oynatılıyor:[/] {Markup.Escape(historyEntry.AnimeTitle)}");
            if (!string.IsNullOrWhiteSpace(mediaTitle))
            {
                AnsiConsole.MarkupLine($"[grey]{Markup.Escape(mediaTitle)}[/]");
            }
        }

        _rpcService.UpdatePlaybackPresence(
            historyEntry.AnimeTitle,
            historyEntry.EpisodeTitle,
            historyEntry.PosterUrl,
            false,
            historyEntry.LastPositionSeconds,
            historyEntry.TotalDurationSeconds,
            historyEntry.ProviderName,
            historyEntry.Season,
            historyEntry.EpisodeNumber);

        await Task.Delay(1000, cancellationToken);

        var monitorTask = Task.Run(() => MonitorIpcAsync(ipcPath, historyEntry, process, cancellationToken),
                                   cancellationToken);

        await process.WaitForExitAsync(cancellationToken);

        _rpcService.ClearPresence();

        foreach (var tempFile in tempFiles)
        {
            try
            {
                if (File.Exists(tempFile))
                {
                    File.Delete(tempFile);
                }
            }
            catch
            {
                /* ignored */
            }
        }

        if (!string.IsNullOrWhiteSpace(tempSubDir) && Directory.Exists(tempSubDir))
        {
            try { Directory.Delete(tempSubDir, true); }
            catch
            {
                // ignored
            }
        }

        if (!isWindows && File.Exists(ipcPath))
        {
            try { File.Delete(ipcPath); }
            catch
            {
                // ignored
            }
        }

        SyncOutcome syncOutcome;
        try
        {
            syncOutcome = await monitorTask;
        }
        catch
        {
            syncOutcome = new SyncOutcome
            {
                Kind = SyncOutcomeKind.Queued
            };
        }

        return syncOutcome;
    }

    private async Task<SyncOutcome> MonitorIpcAsync(string ipcPath,
        WatchHistoryEntry                                  historyEntry,
        Process                                            process,
        CancellationToken                                  cancellationToken)
    {
        var     isWindows = OperatingSystem.IsWindows();
        Stream? stream    = null;
        var syncOutcome = new SyncOutcome
        {
            Kind = SyncOutcomeKind.None
        };

        try
        {
            if (isWindows)
            {
                var pipe = new NamedPipeClientStream(".", ipcPath, PipeDirection.InOut);
                await pipe.ConnectAsync(5000, cancellationToken);
                stream = pipe;
            }
            else
            {
                Socket? socket = null;
                for (var i = 0; i < 15; i++)
                {
                    if (process.HasExited)
                    {
                        break;
                    }

                    try
                    {
                        socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                        await socket.ConnectAsync(new UnixDomainSocketEndPoint(ipcPath), cancellationToken);
                        break;
                    }
                    catch
                    {
                        socket?.Dispose();
                        socket = null;
                        await Task.Delay(300, cancellationToken);
                    }
                }

                if (socket == null)
                {
                    return new SyncOutcome
                    {
                        Kind = SyncOutcomeKind.None
                    };
                }

                stream = new NetworkStream(socket, true);
            }

            using var       reader = new StreamReader(stream, new UTF8Encoding(false), leaveOpen: true);
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true);
            writer.AutoFlush = true;

            await writer.WriteLineAsync("{\"command\":[\"observe_property\",1,\"time-pos\"]}");
            await writer.WriteLineAsync("{\"command\":[\"observe_property\",2,\"duration\"]}");
            await writer.WriteLineAsync("{\"command\":[\"observe_property\",3,\"pause\"]}");

            var lastSave = DateTime.UtcNow;
            var isPaused = false;

            while (!process.HasExited && !cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellationToken);
                if (line == null)
                {
                    break;
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                try
                {
                    using var doc  = JsonDocument.Parse(line);
                    var       root = doc.RootElement;

                    if (root.TryGetProperty("event", out var ev) && ev.GetString() == "property-change")
                    {
                        var id      = root.GetProperty("id").GetInt32();
                        var changed = false;

                        switch (id)
                        {
                            case 1
                                when root.TryGetProperty("data", out var timeData)
                                     && timeData.ValueKind == JsonValueKind.Number:
                                historyEntry.LastPositionSeconds = timeData.GetDouble();
                                break;

                            case 2
                                when root.TryGetProperty("data", out var durData)
                                     && durData.ValueKind == JsonValueKind.Number:
                                historyEntry.TotalDurationSeconds = durData.GetDouble();
                                break;

                            case 3
                                when root.TryGetProperty("data", out var pauseData)
                                     && pauseData.ValueKind is JsonValueKind.True or JsonValueKind.False:
                                isPaused = pauseData.GetBoolean();
                                changed  = true;
                                break;
                        }

                        if (changed || (DateTime.UtcNow - lastSave).TotalSeconds >= 5)
                        {
                            _historyService.SaveWatchProgress(historyEntry);
                            _rpcService.UpdatePlaybackPresence(
                                historyEntry.AnimeTitle,
                                historyEntry.EpisodeTitle,
                                historyEntry.PosterUrl,
                                isPaused,
                                historyEntry.LastPositionSeconds,
                                historyEntry.TotalDurationSeconds,
                                historyEntry.ProviderName,
                                historyEntry.Season,
                                historyEntry.EpisodeNumber
                            );
                            lastSave = DateTime.UtcNow;
                        }
                    }
                }
                catch
                {
                    // ignored
                }
            }
        }
        catch
        {
            // ignored
        }
        finally
        {
            _historyService.SaveWatchProgress(historyEntry);

            if (!_configService.Config.EnableIncognitoMode)
            {
                syncOutcome = await _syncService.SyncAsync(new SyncWatchEntry
                {
                    Provider    = historyEntry.ProviderName,
                    AnimeTitle  = historyEntry.AnimeTitle,
                    AnimeId     = historyEntry.AnimeId,
                    Season      = historyEntry.Season,
                    Episode     = historyEntry.EpisodeNumber,
                    IsCompleted = historyEntry.IsCompleted
                });
            }

            stream?.Dispose();
        }

        return syncOutcome;
    }

    private static string ResolveSubtitleExtension(Subtitle sub)
    {
        if (!string.IsNullOrWhiteSpace(sub.Format))
        {
            var fmt = sub.Format.Trim().TrimStart('.').ToLowerInvariant();
            return fmt switch
            {
                "ass" or "ssa" => ".ass",
                "srt"          => ".srt",
                "vtt"          => ".vtt",
                _              => "." + fmt
            };
        }

        if (sub.Url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            if (sub.Url.Contains("text/vtt", StringComparison.OrdinalIgnoreCase))
            {
                return ".vtt";
            }

            if (sub.Url.Contains("x-subrip", StringComparison.OrdinalIgnoreCase)
                || sub.Url.Contains("text/plain", StringComparison.OrdinalIgnoreCase)
                || sub.Url.Contains("srt", StringComparison.OrdinalIgnoreCase))
            {
                return ".srt";
            }

            return ".ass";
        }

        try
        {
            var urlWithoutQuery = sub.Url.Split('?', '#')[0];
            var ext             = Path.GetExtension(urlWithoutQuery).ToLowerInvariant();
            if (ext is ".ass" or ".ssa" or ".srt" or ".vtt")
            {
                return ext == ".ssa" ? ".ass" : ext;
            }
        }
        catch
        {
            // ignored
        }

        return ".srt";
    }

    private static string BuildSubtitleBaseName(Subtitle sub, int index)
    {
        var raw = !string.IsNullOrWhiteSpace(sub.Label)
                      ? sub.Label
                      : !string.IsNullOrWhiteSpace(sub.Language)
                          ? sub.Language
                          : "altyazi";

        if (!string.IsNullOrWhiteSpace(sub.Label)
            && !string.IsNullOrWhiteSpace(sub.Language)
            && !sub.Label.Contains(sub.Language, StringComparison.OrdinalIgnoreCase))
        {
            raw = $"{raw}-{sub.Language}";
        }

        var sanitized = SanitizeFileName(raw);
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            sanitized = "altyazi";
        }

        return index == 0 ? $"migurdex-{sanitized}" : $"migurdex-{sanitized}-{index + 1}";
    }

    private static string GetUniqueFileName(HashSet<string> usedNames, string baseName, string extension)
    {
        var candidate = baseName + extension;
        var counter   = 2;
        while (!usedNames.Add(candidate))
        {
            candidate = $"{baseName}-{counter}{extension}";
            counter++;
        }

        return candidate;
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb      = new StringBuilder(name.Length);
        foreach (var c in name.Trim())
        {
            if (Array.IndexOf(invalid, c) >= 0
                || c == Path.DirectorySeparatorChar
                || c == Path.AltDirectorySeparatorChar)
            {
                sb.Append('-');
            }
            else if (char.IsWhiteSpace(c))
            {
                sb.Append('-');
            }
            else
            {
                sb.Append(c);
            }
        }

        var result = sb.ToString().Trim('-');
        while (result.Contains("--", StringComparison.Ordinal))
        {
            result = result.Replace("--", "-", StringComparison.Ordinal);
        }

        return result.Length > 60 ? result[..60].Trim('-') : result;
    }

    private static async Task<string?> TryWriteDataUriSubtitleAsync(string dataUri,
        string                                                             destPath,
        CancellationToken                                                  ct)
    {
        try
        {
            var commaIndex = dataUri.IndexOf(',');
            if (commaIndex == -1)
            {
                return null;
            }

            var    header  = dataUri[..commaIndex];
            var    payload = dataUri[(commaIndex + 1)..];
            byte[] bytes;
            if (header.Contains(";base64", StringComparison.OrdinalIgnoreCase))
            {
                bytes = Convert.FromBase64String(payload.Trim());
            }
            else
            {
                bytes = Encoding.UTF8.GetBytes(Uri.UnescapeDataString(payload));
            }

            await File.WriteAllBytesAsync(destPath, bytes, ct);
            return destPath;
        }
        catch
        {
            return null;
        }
    }

    private async Task<string?> TryDownloadSubtitleAsync(Subtitle sub, string destPath, CancellationToken ct)
    {
        const int maxSubtitleBytes = 5 * 1024 * 1024;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, sub.Url);
            if (sub.Headers is { Count: > 0 })
            {
                foreach (var kvp in sub.Headers)
                {
                    if (string.IsNullOrWhiteSpace(kvp.Key) || string.IsNullOrWhiteSpace(kvp.Value))
                    {
                        continue;
                    }

                    request.Headers.TryAddWithoutValidation(kvp.Key.Trim(), kvp.Value.Trim());
                }
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(15));
            using var response =
                await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            response.EnsureSuccessStatusCode();

            var contentLength = response.Content.Headers.ContentLength;
            if (contentLength is > maxSubtitleBytes)
            {
                return null;
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(cts.Token);
            if (bytes.Length is 0 or > maxSubtitleBytes)
            {
                return null;
            }

            if (!IsLikelySubtitleContent(bytes))
            {
                return null;
            }

            var finalPath = RefinePathByContentType(destPath, response.Content.Headers.ContentType?.MediaType);
            await File.WriteAllBytesAsync(finalPath, bytes, ct);
            return finalPath;
        }
        catch
        {
            return null;
        }
    }

    private static string RefinePathByContentType(string destPath, string? mediaType)
    {
        if (string.IsNullOrWhiteSpace(mediaType) || !destPath.EndsWith(".srt", StringComparison.OrdinalIgnoreCase))
        {
            return destPath;
        }

        string? better = null;
        if (mediaType.Contains("vtt", StringComparison.OrdinalIgnoreCase))
        {
            better = ".vtt";
        }
        else if (mediaType.Contains("ssa", StringComparison.OrdinalIgnoreCase)
                 || mediaType.Contains("ass", StringComparison.OrdinalIgnoreCase))
        {
            better = ".ass";
        }

        return better is null ? destPath : Path.ChangeExtension(destPath, better);
    }

    private static bool IsLikelySubtitleContent(byte[] bytes)
    {
        var    probeLen = Math.Min(bytes.Length, 2048);
        string probe;
        try
        {
            probe = Encoding.UTF8.GetString(bytes, 0, probeLen);
        }
        catch
        {
            return false;
        }

        var trimmed = probe.TrimStart('﻿', ' ', '\t', '\r', '\n');
        if (trimmed.StartsWith("<html", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("<!doctype html", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return trimmed.Contains("WEBVTT", StringComparison.OrdinalIgnoreCase)
               || trimmed.Contains("-->", StringComparison.Ordinal)
               || trimmed.Contains("[Script Info]", StringComparison.OrdinalIgnoreCase)
               || trimmed.Contains("[V4+ Styles]", StringComparison.Ordinal)
               || trimmed.Contains("Dialogue:", StringComparison.OrdinalIgnoreCase);
    }

    private static string? FindPlayerExecutable(string playerExe)
    {
        if (string.IsNullOrWhiteSpace(playerExe))
        {
            return null;
        }

        if (playerExe.Contains(Path.DirectorySeparatorChar)
            || playerExe.Contains(Path.AltDirectorySeparatorChar)
            || File.Exists(playerExe))
        {
            return File.Exists(playerExe) ? playerExe : null;
        }

        var candidates = OperatingSystem.IsWindows()
                             ? new[] { playerExe, playerExe + ".exe" }
                             : new[] { playerExe };

        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(pathEnv))
        {
            foreach (var dir in pathEnv.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(dir))
                {
                    continue;
                }

                foreach (var candidate in candidates)
                {
                    try
                    {
                        var fullPath = Path.Combine(dir, candidate);
                        if (File.Exists(fullPath))
                        {
                            return fullPath;
                        }
                    }
                    catch
                    {
                        // ignored
                    }
                }
            }
        }

        return null;
    }
}

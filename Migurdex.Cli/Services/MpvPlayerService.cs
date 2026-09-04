using Migurdex.Cli.Configuration;
using Migurdex.Cli.Utils;
using Migurdex.Shared.Models;
using System.Diagnostics;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Migurdex.Cli.Services;

public class MpvPlayerService : IMpvPlayerService
{
    private readonly IConfigurationService _configService;
    private readonly IHistoryService       _historyService;
    private readonly IDiscordRpcService    _rpcService;

    public MpvPlayerService(
        IConfigurationService configService,
        IHistoryService       historyService,
        IDiscordRpcService    rpcService)
    {
        _configService  = configService;
        _historyService = historyService;
        _rpcService     = rpcService;
    }

    public async Task PlayAsync(
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

        var tempFiles = new List<string>();
        if (subtitles is { Count: > 0 })
        {
            foreach (var sub in subtitles)
            {
                if (string.IsNullOrEmpty(sub.Url))
                {
                    continue;
                }

                var subPath = sub.Url;

                if (sub.Url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        var commaIndex = sub.Url.IndexOf(',');
                        if (commaIndex != -1)
                        {
                            var base64Part = sub.Url[(commaIndex + 1)..];
                            var bytes      = Convert.FromBase64String(base64Part);

                            var extension = ".ass";
                            if (sub.Url.Contains("text/plain") || sub.Url.Contains("application/x-subrip"))
                            {
                                extension = ".srt";
                            }
                            else if (sub.Url.Contains("text/vtt"))
                            {
                                extension = ".vtt";
                            }

                            var tempSubFile =
                                Path.Combine(Path.GetTempPath(), $"migurdex-sub-{Guid.NewGuid():N}{extension}");
                            await File.WriteAllBytesAsync(tempSubFile, bytes, cancellationToken);
                            tempFiles.Add(tempSubFile);
                            subPath = tempSubFile;
                        }
                    }
                    catch
                    {
                        continue;
                    }
                }

                argList.Add($"--sub-file={subPath}");
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
            var season  = Math.Max(1, historyEntry.Season);
            var episode = historyEntry.EpisodeNumber % 1 == 0
                              ? ((int) historyEntry.EpisodeNumber).ToString()
                              : historyEntry.EpisodeNumber.ToString("0.#",
                                                                    System.Globalization.CultureInfo.InvariantCulture);
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

        _ = Task.Run(() => MonitorIpcAsync(ipcPath, historyEntry, process, cancellationToken), cancellationToken);

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

        if (!isWindows && File.Exists(ipcPath))
        {
            try { File.Delete(ipcPath); }
            catch
            {
                // ignored
            }
        }
    }

    private async Task MonitorIpcAsync(string ipcPath,
        WatchHistoryEntry                     historyEntry,
        Process                               process,
        CancellationToken                     cancellationToken)
    {
        var     isWindows = OperatingSystem.IsWindows();
        Stream? stream    = null;

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
                    return;
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
            stream?.Dispose();
        }
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

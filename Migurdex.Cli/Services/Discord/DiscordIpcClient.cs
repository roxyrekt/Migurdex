using Migurdex.Cli.Services.Discord;
using System.Buffers.Binary;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Migurdex.Cli.Services.Discord;

public partial class DiscordIpcClient : IDisposable
{
    private readonly string          _clientId;
    private readonly Action<string>? _logAction;
    private readonly SemaphoreSlim   _lock = new(1, 1);

    private Stream?                  _stream;
    private Socket?                  _unixSocket;
    private NamedPipeClientStream?   _winPipe;
    private CancellationTokenSource? _readCts;
    private Task?                    _readTask;

    private volatile        bool             _isConnected;
    private                 Task<bool>?      _ongoingConnectTask;
    private                 DiscordActivity? _pendingActivity;
    private                 bool             _hasPendingActivity;
    private                 DateTime         _lastConnectAttempt = DateTime.MinValue;
    private static readonly TimeSpan         _reconnectCooldown  = TimeSpan.FromSeconds(3);

    public DiscordIpcClient(string clientId, Action<string>? logAction = null)
    {
        _clientId  = clientId;
        _logAction = logAction;
    }

    public bool IsConnected => _isConnected;

    public Task<bool> EnsureConnectedAsync(CancellationToken ct = default)
    {
        if (_isConnected && _stream != null)
        {
            return Task.FromResult(true);
        }

        lock (_lock)
        {
            if (_isConnected && _stream != null)
            {
                return Task.FromResult(true);
            }

            if (_ongoingConnectTask is { IsCompleted: false })
            {
                return _ongoingConnectTask;
            }

            if (DateTime.UtcNow - _lastConnectAttempt < _reconnectCooldown)
            {
                return Task.FromResult(false);
            }

            _lastConnectAttempt = DateTime.UtcNow;
            _ongoingConnectTask = ConnectInternalAsync(ct);
            return _ongoingConnectTask;
        }
    }

    private async Task<bool> ConnectInternalAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_isConnected && _stream != null)
            {
                return true;
            }

            CleanupCurrentConnection();

            _logAction?.Invoke("Discord IPC bağlantısı kuruluyor...");

            var stream = await ConnectTransportAsync(ct).ConfigureAwait(false);
            if (stream == null)
            {
                _logAction?.Invoke("Discord IPC soketi bulunamadı veya bağlantı reddedildi.");
                return false;
            }

            _stream = stream;

            var handshakePayload = JsonSerializer.Serialize(
                new DiscordHandshake
                {
                    Version  = 1,
                    ClientId = _clientId
                },
                DiscordJsonContext.Default.DiscordHandshake
            );

            await SendFrameInternalAsync(DiscordOpcode.Handshake, handshakePayload, ct).ConfigureAwait(false);

            var (op, respBytes) = await ReadFrameInternalAsync(stream, ct).ConfigureAwait(false);
            if (op != DiscordOpcode.Frame && op != DiscordOpcode.Handshake)
            {
                _logAction?.Invoke($"Discord Handshake başarısız oldu, opcode: {op}");
                CleanupCurrentConnection();
                return false;
            }

            var respJson = Encoding.UTF8.GetString(respBytes);
            _logAction?.Invoke($"Discord RPC bağlandı (Handshake OK): {respJson}");

            _isConnected = true;
            _readCts     = new CancellationTokenSource();
            _readTask    = Task.Run(() => ReadDrainLoopAsync(_stream, _readCts.Token), ct);

            if (_hasPendingActivity)
            {
                var act = _pendingActivity;
                _hasPendingActivity = false;

                _ = Task.Run(async () =>
                             {
                                 try
                                 {
                                     await SendActivityDirectAsync(act, CancellationToken.None).ConfigureAwait(false);
                                 }
                                 catch (Exception ex)
                                 {
                                     _logAction?.Invoke($"Bekleyen activity gönderilemedi: {ex.Message}");
                                 }
                             },
                             ct);
            }

            return true;
        }
        catch (Exception ex)
        {
            _logAction?.Invoke($"Discord IPC bağlantı hatası: {ex.Message}");
            CleanupCurrentConnection();
            return false;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<bool> SetActivityAsync(DiscordActivity? activity, CancellationToken ct = default)
    {
        _pendingActivity    = activity;
        _hasPendingActivity = true;

        var connected = await EnsureConnectedAsync(ct).ConfigureAwait(false);
        if (!connected || _stream == null || !_isConnected)
        {
            return false;
        }

        return await SendActivityDirectAsync(activity, ct).ConfigureAwait(false);
    }

    private async Task<bool> SendActivityDirectAsync(DiscordActivity? activity, CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_isConnected || _stream == null)
            {
                return false;
            }

            var command = new DiscordCommand<SetActivityArgs>
            {
                Command = "SET_ACTIVITY",
                Args = new SetActivityArgs
                {
                    ProcessId = Environment.ProcessId,
                    Activity  = activity
                },
                Nonce = Guid.NewGuid().ToString("N")
            };

            var payload = JsonSerializer.Serialize(
                command,
                DiscordJsonContext.Default.DiscordCommandSetActivityArgs
            );

            await SendFrameInternalAsync(DiscordOpcode.Frame, payload, ct).ConfigureAwait(false);
            _hasPendingActivity = false;
            return true;
        }
        catch (Exception ex)
        {
            _logAction?.Invoke($"SetActivity gönderilirken hata oluştu: {ex.Message}");
            _isConnected = false;
            CleanupCurrentConnection();
            return false;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<bool> ClearActivityAsync(CancellationToken ct = default)
    {
        _pendingActivity    = null;
        _hasPendingActivity = false;
        return await SetActivityAsync(null, ct).ConfigureAwait(false);
    }

    private async Task<Stream?> ConnectTransportAsync(CancellationToken ct)
    {
        if (OperatingSystem.IsWindows())
        {
            for (var i = 0; i < 10; i++)
            {
                try
                {
                    var pipe = new NamedPipeClientStream(".",
                                                         $"discord-ipc-{i}",
                                                         PipeDirection.InOut,
                                                         PipeOptions.Asynchronous);

                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    cts.CancelAfter(250);
                    await pipe.ConnectAsync(cts.Token).ConfigureAwait(false);
                    _winPipe = pipe;
                    return pipe;
                }
                catch
                {
                    // ignored
                }
            }

            return null;
        }

        var socketPaths = GetUnixCandidatePaths();
        foreach (var path in socketPaths)
        {
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified)
                {
                    ReceiveTimeout = 2000,
                    SendTimeout    = 2000
                };

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(250);
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(path), cts.Token).ConfigureAwait(false);

                _unixSocket = socket;
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex)
            {
                _logAction?.Invoke($"Socket bağlantı denemesi başarısız ({path}): {ex.Message}");
            }
        }

        return null;
    }

    private static List<string> GetUnixCandidatePaths()
    {
        var dirs = new List<string>();

        var xdgRuntime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        if (!string.IsNullOrWhiteSpace(xdgRuntime))
        {
            dirs.Add(xdgRuntime);
            dirs.Add(Path.Combine(xdgRuntime, "app", "com.discordapp.Discord"));
            dirs.Add(Path.Combine(xdgRuntime, "app", "dev.vencord.Vesktop"));
            dirs.Add(Path.Combine(xdgRuntime, "app", "com.discordapp.DiscordCanary"));
            dirs.Add(Path.Combine(xdgRuntime, "app", "com.discordapp.DiscordPTB"));
        }

        try
        {
            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                var uid = GetUnixEuid();
                if (uid >= 0)
                {
                    dirs.Add($"/run/user/{uid}");
                    dirs.Add($"/run/user/{uid}/app/com.discordapp.Discord");
                    dirs.Add($"/run/user/{uid}/app/dev.vencord.Vesktop");
                }
            }
        }
        catch
        {
            // ignored
        }

        var tmpDir = Environment.GetEnvironmentVariable("TMPDIR");
        if (!string.IsNullOrWhiteSpace(tmpDir))
        {
            dirs.Add(tmpDir);
        }

        var tmp = Environment.GetEnvironmentVariable("TMP");
        if (!string.IsNullOrWhiteSpace(tmp))
        {
            dirs.Add(tmp);
        }

        var temp = Environment.GetEnvironmentVariable("TEMP");
        if (!string.IsNullOrWhiteSpace(temp))
        {
            dirs.Add(temp);
        }

        dirs.Add("/tmp");

        var distinctDirs = dirs.Distinct().ToList();
        var paths        = new List<string>();

        for (var i = 0; i < 10; i++)
        {
            foreach (var dir in distinctDirs)
            {
                paths.Add(Path.Combine(dir, $"discord-ipc-{i}"));
            }
        }

        return paths;
    }

    private static int GetUnixEuid()
    {
        try
        {
            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                return (int) geteuid();
            }
        }
        catch
        {
            // ignored
        }

        return -1;
    }

    [DllImport("libc", EntryPoint = "geteuid", SetLastError = true)]
    private static extern uint geteuid();

    private async Task SendFrameInternalAsync(DiscordOpcode op, string payloadJson, CancellationToken ct)
    {
        if (_stream == null)
        {
            throw new InvalidOperationException("Stream is null");
        }

        var payloadBytes = Encoding.UTF8.GetBytes(payloadJson);
        var header       = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(0, 4), (int) op);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4, 4), payloadBytes.Length);

        await _stream.WriteAsync(header, ct).ConfigureAwait(false);
        if (payloadBytes.Length > 0)
        {
            await _stream.WriteAsync(payloadBytes, ct).ConfigureAwait(false);
        }

        await _stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private static async Task<(DiscordOpcode Opcode, byte[] Payload)> ReadFrameInternalAsync(Stream stream,
        CancellationToken                                                                           ct)
    {
        var header = new byte[8];
        await ReadExactAsync(stream, header, ct).ConfigureAwait(false);

        var op  = (DiscordOpcode) BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(0, 4));
        var len = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4, 4));

        if (len is < 0 or > 1024 * 1024)
        {
            throw new InvalidDataException($"Geçersiz Discord IPC frame uzunluğu: {len}");
        }

        var payload = new byte[len];
        if (len > 0)
        {
            await ReadExactAsync(stream, payload, ct).ConfigureAwait(false);
        }

        return (op, payload);
    }

    private static async Task ReadExactAsync(Stream stream, Memory<byte> buffer, CancellationToken ct)
    {
        var totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[totalRead..], ct).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException("Discord IPC bağlantısı beklenmedik şekilde kapandı.");
            }

            totalRead += read;
        }
    }

    private async Task ReadDrainLoopAsync(Stream stream, CancellationToken ct)
    {
        var headerBuf = new byte[8];
        var drainBuf  = new byte[4096];

        try
        {
            while (!ct.IsCancellationRequested)
            {
                await ReadExactAsync(stream, headerBuf, ct).ConfigureAwait(false);
                var op  = (DiscordOpcode) BinaryPrimitives.ReadInt32LittleEndian(headerBuf.AsSpan(0, 4));
                var len = BinaryPrimitives.ReadInt32LittleEndian(headerBuf.AsSpan(4, 4));

                if (op == DiscordOpcode.Close)
                {
                    _logAction?.Invoke("Discord IPC sunucudan Close sinyali aldı.");
                    break;
                }

                if (op == DiscordOpcode.Ping)
                {
                    _ = Task.Run(async () =>
                                 {
                                     try
                                     {
                                         await _lock.WaitAsync(ct).ConfigureAwait(false);
                                         try
                                         {
                                             if (_stream != null)
                                             {
                                                 await SendFrameInternalAsync(DiscordOpcode.Pong, "{}", ct)
                                                     .ConfigureAwait(false);
                                             }
                                         }
                                         finally
                                         {
                                             _lock.Release();
                                         }
                                     }
                                     catch
                                     {
                                         // ignored
                                     }
                                 },
                                 ct);
                }

                var remaining = len;
                while (remaining > 0)
                {
                    var toRead = Math.Min(remaining, drainBuf.Length);
                    var read   = await stream.ReadAsync(drainBuf.AsMemory(0, toRead), ct).ConfigureAwait(false);
                    if (read == 0)
                    {
                        throw new EndOfStreamException();
                    }

                    remaining -= read;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logAction?.Invoke($"Discord IPC dinleme döngüsü sonlandı: {ex.Message}");
        }
        finally
        {
            _isConnected = false;
        }
    }

    private void CleanupCurrentConnection()
    {
        _isConnected = false;

        try
        {
            _readCts?.Cancel();
            _readCts?.Dispose();
        }
        catch
        {
            // ignored
        }

        _readCts = null;

        try
        {
            _stream?.Dispose();
        }
        catch
        {
            // ignored
        }

        _stream = null;

        try
        {
            _unixSocket?.Dispose();
        }
        catch
        {
            // ignored
        }

        _unixSocket = null;

        try
        {
            _winPipe?.Dispose();
        }
        catch
        {
            // ignored
        }

        _winPipe = null;
    }

    public void Dispose()
    {
        CleanupCurrentConnection();
        _lock.Dispose();
        GC.SuppressFinalize(this);
    }
}

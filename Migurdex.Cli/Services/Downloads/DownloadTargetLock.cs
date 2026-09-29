using System.Collections.Concurrent;

namespace Migurdex.Cli.Services.Downloads;

internal static class DownloadTargetLock
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _processLocks =
        new(StringComparer.Ordinal);

    public static async Task<IDisposable> AcquireAsync(
        string            targetIdentity,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(targetIdentity))
        {
            throw new ArgumentException("Hedef yolu boş olamaz.", nameof(targetIdentity));
        }

        var fullTarget = Path.GetFullPath(targetIdentity);
        var key         = PathKey(fullTarget);
        var processLock = _processLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await processLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        FileStream? stream = null;
        var         lockPath = BuildLockPath(fullTarget);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            DownloadPathBuilder.EnsureFullPathBudget(lockPath, reservedSuffixBytes: 0);
            stream = new FileStream(lockPath,
                                     FileMode.OpenOrCreate,
                                     FileAccess.ReadWrite,
                                     FileShare.None,
                                     bufferSize: 1,
                                     FileOptions.SequentialScan);
            return new LockHandle(processLock, lockPath, stream);
        }
        catch (ConcurrentDownloadException)
        {
            stream?.Dispose();
            processLock.Release();
            throw;
        }
        catch (IOException ex)
        {
            stream?.Dispose();
            processLock.Release();
            throw new ConcurrentDownloadException(
                "Video hedefi başka bir indirme tarafından kullanılıyor.",
                ex);
        }
        catch
        {
            stream?.Dispose();
            processLock.Release();
            throw;
        }
    }

    private static string BuildLockPath(string targetIdentity)
    {
        var directory = Path.GetDirectoryName(targetIdentity);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return targetIdentity + ".migurdex.lock";
        }

        var name = Path.GetFileName(targetIdentity);
        if (name.EndsWith(".migurdex.lock", StringComparison.OrdinalIgnoreCase))
        {
            return targetIdentity;
        }

        return Path.Combine(directory, name + ".migurdex.lock");
    }

    private static string PathKey(string path)
    {
        return OperatingSystem.IsWindows() ? path.ToUpperInvariant() : path;
    }

    private sealed class LockHandle : IDisposable
    {
        private readonly SemaphoreSlim _processLock;
        private readonly string       _lockPath;

        private FileStream? _stream;

        public LockHandle(SemaphoreSlim processLock, string lockPath, FileStream stream)
        {
            _processLock = processLock;
            _lockPath    = lockPath;
            _stream      = stream;
        }

        public void Dispose()
        {
            var stream = Interlocked.Exchange(ref _stream, null);
            if (stream is null)
            {
                return;
            }

            try
            {
                stream.Dispose();
            }
            catch
            {
                // ignored
            }

            for (var attempt = 0; attempt < 10 && File.Exists(_lockPath); attempt++)
            {
                try
                {
                    File.Delete(_lockPath);
                }
                catch
                {
                    // Windows'ta handle kapanması bir sonraki retry'a kalmayabilir.
                }

                if (File.Exists(_lockPath))
                {
                    Thread.Sleep(10);
                }
            }

            _processLock.Release();
        }
    }
}

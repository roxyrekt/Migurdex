using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace Migurdex.Cli.Dependencies;

public static class DependencyArchive
{
    private const long MaxTotalUncompressedBytes = 2L * 1024 * 1024 * 1024;
    private const int  BufferSize                = 81920;
    private const long DetailedProgressBytes     = 8L * 1024 * 1024;

    private static ReaderOptions Options()
    {
        return ReaderOptions.ForFilePath;
    }

    public static void ExtractToDirectory(
        string             archivePath,
        string             destinationDirectory,
        IProgress<string>? progress          = null,
        CancellationToken  cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);

        Directory.CreateDirectory(destinationDirectory);

        var root = Path.GetFullPath(destinationDirectory)
                       .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                   + Path.DirectorySeparatorChar;

        using var archive = ArchiveFactory.OpenArchive(archivePath, Options());

        var entries = archive.Entries
                             .Where(e => !e.IsDirectory
                                         && !e.IsEncrypted
                                         && string.IsNullOrEmpty(e.LinkTarget)
                                         && ResolveTargetPath(root, e.Key) is not null)
                             .ToList();

        long totalBytes  = 0;
        var  doneEntries = 0;

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var target = ResolveTargetPath(root, entry.Key)!;

            totalBytes += entry.Size;
            if (totalBytes > MaxTotalUncompressedBytes)
            {
                throw new InvalidDataException("Arşiv açıldığında beklenenden fazla yer kaplıyor.");
            }

            var label = entry.Size >= DetailedProgressBytes
                            ? $"Arşiv açılıyor: {Path.GetFileName(entry.Key)}"
                            : "Arşiv açılıyor";

            CopyEntry(entry, target, label, progress, cancellationToken);

            doneEntries += 1;

            if (entry.Size < DetailedProgressBytes)
            {
                progress?.Report($"Arşiv açılıyor: {doneEntries}/{entries.Count}");
            }
        }
    }

    public static bool TryExtractFile(
        string             archivePath,
        string             fileName,
        string             destinationPath,
        IProgress<string>? progress          = null,
        CancellationToken  cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        cancellationToken.ThrowIfCancellationRequested();

        using var archive = ArchiveFactory.OpenArchive(archivePath, Options());
        var entry = archive.Entries.FirstOrDefault(e => !e.IsDirectory
                                                        && string.Equals(Path.GetFileName(e.Key),
                                                                         fileName,
                                                                         StringComparison.OrdinalIgnoreCase));
        if (entry is null || entry.IsEncrypted || !string.IsNullOrEmpty(entry.LinkTarget))
        {
            return false;
        }

        var fullDestination = Path.GetFullPath(destinationPath);

        CopyEntry(entry, fullDestination, $"Arşivden çıkarılıyor: {fileName}", progress, cancellationToken);

        return true;
    }

    public static bool ContainsFile(string archivePath, string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        using var archive = ArchiveFactory.OpenArchive(archivePath, Options());

        return archive.Entries.Any(e => !e.IsDirectory
                                        && string.Equals(Path.GetFileName(e.Key),
                                                         fileName,
                                                         StringComparison.OrdinalIgnoreCase));
    }

    private static void CopyEntry(
        IArchiveEntry      entry,
        string             targetPath,
        string             label,
        IProgress<string>? progress,
        CancellationToken  cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

        using var input = entry.OpenEntryStream();
        using var output = new FileStream(targetPath,
                                          FileMode.Create,
                                          FileAccess.Write,
                                          FileShare.None,
                                          BufferSize);

        var total = entry.Size;

        if (total <= 0)
        {
            input.CopyTo(output);

            return;
        }

        progress?.Report($"{label}: %0");

        var  buffer      = new byte[BufferSize];
        long copied      = 0;
        var  lastPercent = -1;

        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            output.Write(buffer, 0, read);
            copied += read;

            var percent = (int) (copied * 100 / total);
            if (percent < lastPercent + 5 && !(percent == 100 && lastPercent < 100))
            {
                continue;
            }

            lastPercent = percent;
            progress?.Report($"{label}: %{percent}");
        }
    }

    private static string? ResolveTargetPath(string root, string? entryKey)
    {
        if (string.IsNullOrWhiteSpace(entryKey))
        {
            return null;
        }

        if (Path.IsPathRooted(entryKey) || entryKey.Contains(':'))
        {
            return null;
        }

        var relative = entryKey.Replace('\\', Path.DirectorySeparatorChar)
                               .Replace('/', Path.DirectorySeparatorChar)
                               .TrimStart(Path.DirectorySeparatorChar);

        var fullPath = Path.GetFullPath(Path.Combine(root, relative));

        return fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? fullPath : null;
    }
}

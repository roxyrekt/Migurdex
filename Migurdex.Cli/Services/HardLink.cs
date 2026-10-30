using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Migurdex.Cli.Services;

internal static class HardLink
{
    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int LinkUnix(string existingPath, string newPath);

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLinkWindows(string newPath,
        string                                              existingPath,
        IntPtr                                              securityAttributes);

    public static bool TryCreate(string existingPath, string newPath)
    {
        if (!File.Exists(existingPath))
        {
            return false;
        }

        var tmp = newPath + ".hltmp";

        try
        {
            TryDelete(tmp);

            var ok = OperatingSystem.IsWindows()
                         ? CreateHardLinkWindows(tmp, existingPath, IntPtr.Zero)
                         : LinkUnix(existingPath, tmp) == 0;

            if (!ok)
            {
                TryDelete(tmp);

                return false;
            }

            File.Move(tmp, newPath, true);

            // hedef zaten aynı inode'a bağlıysa posix rename() no-op olur ve
            // geçici dosya diskte kalır (link count 3'e çıkar, her dedupe da
            // bir .hltmp birikir) koşulsuz temizle
            TryDelete(tmp);

            return true;
        }
        catch
        {
            TryDelete(tmp);

            return false;
        }
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
            // ignored
        }
    }
}

internal static class HardLinkDeduplicator
{
    public static (int Linked, long SavedBytes) Run(string cliDir, string apiDir)
    {
        if (!Directory.Exists(cliDir) || !Directory.Exists(apiDir))
        {
            return (0, 0);
        }

        // Düz yerleşimde (Windows) CLI köktedir ve API api/ altındadır. Kökü
        // derinlemesine gezmek api/ içindekileri de tarar -> api/api/x gibi
        // saçma hedefler. Bu durumda yalnızca üst düzey dosyalara bak.
        var searchOption = IsFlatLayout(cliDir, apiDir)
                               ? SearchOption.TopDirectoryOnly
                               : SearchOption.AllDirectories;

        var  linked = 0;
        long saved  = 0;

        foreach (var src in Directory.EnumerateFiles(cliDir, "*", searchOption))
        {
            var rel = Path.GetRelativePath(cliDir, src);
            var dst = Path.Combine(apiDir, rel);

            if (!File.Exists(dst))
            {
                continue;
            }

            try
            {
                var srcInfo = new FileInfo(src);
                var dstInfo = new FileInfo(dst);

                if (srcInfo.Length != dstInfo.Length || srcInfo.Length == 0)
                {
                    continue;
                }

                if (!HaveSameContent(src, dst))
                {
                    continue;
                }

                var size = srcInfo.Length;

                if (HardLink.TryCreate(dst, src))
                {
                    linked++;
                    saved += size;
                }
            }
            catch
            {
                // ignored
            }
        }

        return (linked, saved);
    }

    private static bool HaveSameContent(string a, string b)
    {
        using var sa = File.OpenRead(a);
        using var sb = File.OpenRead(b);

        return SHA256.HashData(sa).AsSpan().SequenceEqual(SHA256.HashData(sb));
    }

    /// <summary>
    ///     Düz yerleşim: CLI kökte, API <c>api/</c> altında, <c>cli/</c> yok (Windows).
    /// </summary>
    private static bool IsFlatLayout(string cliDir, string apiDir)
    {
        var root = Path.GetFullPath(cliDir);
        var api  = Path.GetFullPath(apiDir);

        return api.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)
               && !Directory.Exists(Path.Combine(root, "cli"));
    }
}

namespace Migurdex.Cli.Services;

internal static class InstallLayout
{
    public static string ResolveInstallRoot()
    {
        var baseDir = TrimSeparator(AppContext.BaseDirectory);
        if (string.IsNullOrEmpty(baseDir))
        {
            return AppContext.BaseDirectory;
        }

        var parent = Path.GetDirectoryName(baseDir);
        if (!string.IsNullOrEmpty(parent)
            && Path.GetFileName(baseDir).Equals("cli", StringComparison.OrdinalIgnoreCase)
            && Directory.Exists(Path.Combine(parent, "api")))
        {
            return parent;
        }

        return baseDir;
    }

    public static string ResolveCliDir(string installRoot)
    {
        var cliDir = Path.Combine(installRoot, "cli");

        return Directory.Exists(cliDir) ? cliDir : installRoot;
    }

    public static string ResolveApiDir(string installRoot)
    {
        return Path.Combine(installRoot, "api");
    }

    public static void EnsureRootLauncher(string installRoot)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var cliDir = ResolveCliDir(installRoot);
        if (cliDir == installRoot)
        {
            return;
        }

        var launcher = Path.Combine(cliDir, "migurdex");
        if (!File.Exists(launcher))
        {
            return;
        }

        var link = Path.Combine(installRoot, "migurdex");

        try
        {
            var info = new FileInfo(link);
            if (info.LinkTarget is not null)
            {
                if (info.LinkTarget.Replace('\\', '/').Equals("cli/migurdex", StringComparison.Ordinal))
                {
                    return;
                }

                info.Delete();
            }
            else if (info.Exists)
            {
                info.Delete();
            }

            File.CreateSymbolicLink(link, Path.Combine("cli", "migurdex"));
        }
        catch
        {
            // ignored
        }
    }

    public static bool IsSplitLayout(string installRoot)
    {
        return Directory.Exists(Path.Combine(installRoot, "cli"))
               && Directory.Exists(Path.Combine(installRoot, "api"));
    }

    public static bool CanShareRuntime(string installRoot)
    {
        var cliDir = ResolveCliDir(installRoot);
        var apiDir = ResolveApiDir(installRoot);

        return Directory.Exists(cliDir)
               && Directory.Exists(apiDir)
               && !Path.GetFullPath(cliDir)
                       .Equals(Path.GetFullPath(apiDir), StringComparison.Ordinal);
    }

    private static string TrimSeparator(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return string.Empty;
        }

        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return string.IsNullOrEmpty(trimmed) ? path : trimmed;
    }
}

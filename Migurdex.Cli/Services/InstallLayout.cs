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

    private const string LauncherScript =
        """
        #!/bin/sh

        self=$0
        if command -v readlink >/dev/null 2>&1; then
            resolved=$(readlink -f "$0" 2>/dev/null) && [ -n "$resolved" ] && self=$resolved
        fi
        dir=$(CDPATH= cd -- "$(dirname -- "$self")" && pwd)
        exec "$dir/cli/migurdex" "$@"
        """;

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
                info.Delete();
            }
            else if (info.Exists && info.Length == LauncherScript.Length)
            {
                return;
            }
            else if (info.Exists)
            {
                info.Delete();
            }

            File.WriteAllText(link, LauncherScript);
            File.SetUnixFileMode(link,
                                 UnixFileMode.UserRead
                                 | UnixFileMode.UserWrite
                                 | UnixFileMode.UserExecute
                                 | UnixFileMode.GroupRead
                                 | UnixFileMode.GroupExecute
                                 | UnixFileMode.OtherRead
                                 | UnixFileMode.OtherExecute);
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

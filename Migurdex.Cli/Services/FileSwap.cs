using System.Diagnostics;
using System.Text;

namespace Migurdex.Cli.Services;

internal static class FileSwap
{
    public static bool TryReplace(string src, string dest)
    {
        if (!File.Exists(src))
        {
            return false;
        }

        try
        {
            File.Move(src, dest, true);

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // ignored
        }

        return TrySwapViaBackup(src, dest);
    }

    public static int RecoverLeftovers(string installRoot)
    {
        var healed = 0;

        foreach (var file in Enumerate(installRoot))
        {
            if (!file.EndsWith(".new", StringComparison.Ordinal))
            {
                continue;
            }

            var dest = file[..^4];

            try
            {
                if (File.Exists(dest)
                    && new FileInfo(dest).LastWriteTimeUtc > new FileInfo(file).LastWriteTimeUtc)
                {
                    continue;
                }
            }
            catch
            {
                // ignored
            }

            if (TryReplace(file, dest))
            {
                healed++;
            }
        }

        return healed;
    }

    public static int RemoveBackups(string installRoot)
    {
        var removed = 0;

        foreach (var file in Enumerate(installRoot))
        {
            var name = Path.GetFileName(file);

            if (!name.EndsWith(".old", StringComparison.Ordinal)
                && !name.Contains(".old.", StringComparison.Ordinal))
            {
                continue;
            }

            if (!File.Exists(OriginalOf(file)))
            {
                continue;
            }

            try
            {
                File.Delete(file);
                removed++;
            }
            catch
            {
                // ignored
            }
        }

        return removed;
    }

    public static bool ScheduleDeferredApply(IReadOnlyList<string> pending, string? relaunchExe, string[] relaunchArgs)
    {
        if (!OperatingSystem.IsWindows() || pending.Count == 0)
        {
            return false;
        }

        var installRoot = InstallLayout.ResolveInstallRoot();
        var script      = Path.Combine(Path.GetTempPath(), $"migurdex-apply-{Guid.NewGuid():N}.cmd");

        var sb = new StringBuilder();
        sb.AppendLine("@echo off");
        sb.AppendLine("setlocal");
        sb.AppendLine($"call :waitpid {Environment.ProcessId}");

        foreach (var rel in pending)
        {
            var dest = Path.Combine(installRoot, rel.Replace('/', Path.DirectorySeparatorChar));
            sb.AppendLine($"call :swap \"{dest}.new\" \"{dest}\"");
        }

        sb.AppendLine($"call :clean \"{installRoot}\"");

        if (!string.IsNullOrWhiteSpace(relaunchExe))
        {
            var args = string.Join(' ', relaunchArgs.Select(a => $"\"{a}\""));
            sb.AppendLine($"start \"\" \"{relaunchExe}\" {args}");
        }

        sb.AppendLine("del /f /q \"%~f0\" >nul 2>&1");
        sb.AppendLine("exit /b");
        sb.AppendLine();
        sb.AppendLine(":waitpid");
        sb.AppendLine("for /l %%i in (1,1,120) do (");
        sb.AppendLine($"  tasklist /FI \"PID eq %~1\" /NH 2>nul | findstr /R /C:\" %~1 \" >nul || exit /b");
        sb.AppendLine("  ping -n 2 127.0.0.1 >nul");
        sb.AppendLine(")");
        sb.AppendLine("exit /b");
        sb.AppendLine();
        sb.AppendLine(":swap");
        sb.AppendLine("for /l %%i in (1,1,120) do (");
        sb.AppendLine("  if not exist \"%~1\" exit /b");
        sb.AppendLine("  move /y \"%~1\" \"%~2\" >nul 2>&1");
        sb.AppendLine("  if not exist \"%~1\" exit /b");
        sb.AppendLine("  ping -n 2 127.0.0.1 >nul");
        sb.AppendLine(")");
        sb.AppendLine("exit /b");
        sb.AppendLine();
        sb.AppendLine(":clean");
        sb.AppendLine("for /r \"%~1\" %%f in (*.old) do del /f /q \"%%f\" >nul 2>&1");
        sb.AppendLine("exit /b");

        try
        {
            File.WriteAllText(script, sb.ToString(), Encoding.ASCII);

            var psi = new ProcessStartInfo
            {
                FileName               = "cmd.exe",
                Arguments              = $"/c \"\"{script}\"\"",
                UseShellExecute        = false,
                CreateNoWindow         = true,
                WindowStyle            = ProcessWindowStyle.Hidden,
                WorkingDirectory       = installRoot,
                RedirectStandardOutput = false,
                RedirectStandardError  = false
            };

            return Process.Start(psi) is not null;
        }
        catch
        {
            return false;
        }
    }

    private static string OriginalOf(string backup)
    {
        var name = Path.GetFileName(backup);
        var dot  = name.IndexOf(".old", StringComparison.Ordinal);

        return Path.Combine(Path.GetDirectoryName(backup)!, name[..dot]);
    }

    private static bool TrySwapViaBackup(string src, string dest)
    {
        if (!File.Exists(dest))
        {
            try
            {
                File.Move(src, dest);

                return true;
            }
            catch
            {
                return false;
            }
        }

        var backup = dest + ".old";

        try
        {
            TryDelete(backup);

            if (File.Exists(backup))
            {
                backup = dest + ".old." + Guid.NewGuid().ToString("N")[..8];
            }

            File.Move(dest, backup);
        }
        catch
        {
            return false;
        }

        try
        {
            File.Move(src, dest);

            return true;
        }
        catch
        {
            try
            {
                File.Move(backup, dest, true);
            }
            catch
            {
                // ignored
            }

            return false;
        }
    }

    private static IEnumerable<string> Enumerate(string root)
    {
        foreach (var (dir, depth) in Scopes(root))
        {
            if (!Directory.Exists(dir))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(dir, "*", depth))
            {
                yield return file;
            }
        }
    }

    private static IEnumerable<(string Dir, SearchOption Depth)> Scopes(string root)
    {
        yield return (root, SearchOption.TopDirectoryOnly);
        yield return (Path.Combine(root, "cli"), SearchOption.AllDirectories);
        yield return (Path.Combine(root, "api"), SearchOption.AllDirectories);
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

using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;
using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Migurdex.Cli.Services;

public static class UpdateCommand
{
    [DllImport("libc", EntryPoint = "execvp", SetLastError = true)]
    private static extern int execvp(string file, IntPtr[] argv);

    public static async Task<int> RunAsync(string[] args, IServiceProvider services)
    {
        var     checkOnly  = false;
        var     assumeYes  = false;
        var     noRestart  = false;
        var     showHelp   = false;
        string? channelArg = null;

        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a.Equals("--check", StringComparison.OrdinalIgnoreCase))
            {
                checkOnly = true;
            }
            else if (a.Equals("--no-restart", StringComparison.OrdinalIgnoreCase))
            {
                noRestart = true;
            }
            else if (a is "--yes" or "-y")
            {
                assumeYes = true;
            }
            else if (a is "--help" or "-h")
            {
                showHelp = true;
            }
            else if (a.Equals("--channel", StringComparison.OrdinalIgnoreCase))
            {
                if (++i >= args.Length || string.IsNullOrWhiteSpace(args[i]))
                {
                    AnsiConsole.MarkupLine("[red]--channel için değer gerekli (stable|prerelease).[/]");
                    return 2;
                }

                channelArg = args[i];
            }
            else
            {
                AnsiConsole.MarkupLine($"[red]Bilinmeyen bayrak:[/] {a}");
                return 2;
            }
        }

        if (showHelp)
        {
            Help();
            return 0;
        }

        var updateService = services.GetRequiredService<IUpdateService>();
        var configService = services.GetRequiredService<IConfigurationService>();

        UpdateCheckResult? result = null;
        await AnsiConsole.Status()
                         .Spinner(Spinner.Known.Dots)
                         .StartAsync("Sürüm kontrol ediliyor...",
                                     async _ =>
                                     {
                                         result = await updateService.CheckForUpdatesAsync(true, channelArg);
                                     });

        if (result is null)
        {
            AnsiConsole.MarkupLine("[red]Sürüm kontrolü yapılamadı (çevrimdışı olabilir).[/]");
            return 1;
        }

        AnsiConsole.MarkupLine($"[grey]Mevcut:[/] v{Markup.Escape(result.CurrentVersion)}  "
                               + $"[grey]Son:[/] v{Markup.Escape(result.LatestVersion)}"
                               + (result.IsPrerelease ? " [yellow](pre-release)[/]" : ""));

        if (!result.IsUpdateAvailable)
        {
            AnsiConsole.MarkupLine("[green]Zaten güncelsin.[/]");
            return 0;
        }

        PrintNotes(result);

        if (checkOnly)
        {
            AnsiConsole.MarkupLine("[cyan]Güncellemek için:[/] migurdex update");
            return 0;
        }

        var installType = updateService.DetectInstallType();
        if (installType != InstallType.Managed)
        {
            PrintManualInstructions(installType, result);
            return 2;
        }

        if (string.IsNullOrWhiteSpace(result.AssetUrl) || string.IsNullOrWhiteSpace(result.AssetName))
        {
            AnsiConsole.MarkupLine("[yellow]Bu platform için paket bulunamadı, manuel kurun:[/]");
            AnsiConsole.WriteLine(result.ReleaseUrl);
            return 2;
        }

        if (updateService.IsMpvRunning()
            && !assumeYes
            && !AnsiConsole.Confirm("MPV çalışıyor görünüyor. Oynatma bölünebilir, yine de devam edilsin mi?", false))
        {
            return 0;
        }

        if (!assumeYes
            && !AnsiConsole.Confirm($"v{result.LatestVersion} kurulsun mu? (API kısa süreliğine durur)"))
        {
            return 0;
        }

        var updateStatus = await ApplyUpdateAsync(result);
        if (updateStatus != 0)
        {
            return updateStatus;
        }

        if (!noRestart && !Console.IsInputRedirected)
        {
            AnsiConsole.MarkupLine("[cyan]Yeni sürümle yeniden başlatılıyor...[/]");
            RestartIntoNewVersion();
        }

        return 0;
    }

    private static void PrintNotes(UpdateCheckResult result)
    {
        if (string.IsNullOrWhiteSpace(result.ReleaseNotes))
        {
            return;
        }

        var notes = result.ReleaseNotes.Trim();
        if (notes.Length > 800)
        {
            notes = notes[..800].TrimEnd() + "...";
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[grey]Sürüm notları:[/]");
        AnsiConsole.WriteLine(notes);
        AnsiConsole.WriteLine();
    }

    private static void PrintManualInstructions(InstallType type, UpdateCheckResult result)
    {
        switch (type)
        {
            case InstallType.AppImage:
                AnsiConsole.MarkupLine("[yellow]AppImage kendini güncelleyemez. Yeni dosyayı indirip değiştirin:[/]");
                break;
            case InstallType.Source:
                AnsiConsole.MarkupLine("[yellow]Kaynak kurulum tespit edildi, paket güncellenemez. Şunu yapın:[/]");
                AnsiConsole.WriteLine("  git pull && ./build.sh");
                break;
            default:
                AnsiConsole.MarkupLine("[yellow]Kurulum dizini tanınamadı, manuel kurun:[/]");
                break;
        }

        AnsiConsole.WriteLine($"  {result.ReleaseUrl}");
    }

    private static async Task<int> ApplyUpdateAsync(UpdateCheckResult result)
    {
        var installRoot = AppContext.BaseDirectory;
        var tmpDir      = Path.Combine(Path.GetTempPath(), $"migurdex-update-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);

        try
        {
            StopApiDaemon();

            var archivePath = Path.Combine(tmpDir, result.AssetName!);
            AnsiConsole.MarkupLine($"[cyan]İndiriliyor:[/] {result.AssetName}"
                                   + (result.AssetSize is > 0
                                          ? $" ({result.AssetSize / 1024 / 1024} MB)"
                                          : ""));

            if (!await DownloadFileAsync(result.AssetUrl!, archivePath))
            {
                AnsiConsole.MarkupLine("[red]Paket indirilemedi.[/]");
                return 1;
            }

            if (!await VerifyChecksumAsync(result, archivePath))
            {
                return 1;
            }

            var extractDir = Path.Combine(tmpDir, "extracted");
            Directory.CreateDirectory(extractDir);

            AnsiConsole.MarkupLine("[cyan]Paket açılıyor...[/]");
            try
            {
                ExtractArchive(archivePath, extractDir);
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Paket açılamadı: {Markup.Escape(ex.Message)}[/]");
                return 1;
            }

            var (copied, deferred, backedUp) = CopyTree(FindPayloadRoot(extractDir), installRoot);
            AnsiConsole.MarkupLine($"[green]Güncellendi:[/] {copied} dosya yazıldı.");

            if (backedUp.Count > 0)
            {
                AnsiConsole.MarkupLine("[grey]Önceki sürüm yedeği:[/]");
                foreach (var f in backedUp)
                {
                    AnsiConsole.WriteLine($"  {f}");
                }
            }

            if (deferred.Count > 0)
            {
                AnsiConsole.MarkupLine("[yellow]Kilitli olduğu için yazılamayan dosya:[/]");
                foreach (var f in deferred)
                {
                    AnsiConsole.WriteLine($"  {f} (.new uzantısıyla yazıldı, manuel olarak yeniden adlandırın)");
                }

                return 2;
            }

            return 0;
        }
        finally
        {
            try { Directory.Delete(tmpDir, true); }
            catch
            {
                // ignored
            }
        }
    }

    private static void StopApiDaemon()
    {
        try
        {
            foreach (var p in Process.GetProcessesByName("Migurdex.Api"))
            {
                try
                {
                    p.Kill();
                }
                catch
                {
                    // ignored
                }
            }

            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (DateTime.UtcNow < deadline)
            {
                if (Process.GetProcessesByName("Migurdex.Api").Length == 0)
                {
                    break;
                }

                Thread.Sleep(200);
            }
        }
        catch
        {
            // ignored
        }
    }

    private static async Task<bool> DownloadFileAsync(string url, string destPath)
    {
        try
        {
            using var client = new HttpClient
            {
                Timeout = TimeSpan.FromMinutes(10)
            };
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "migurdex");

            await using var src = await client.GetStreamAsync(url);
            await using var dst = File.Create(destPath);
            await src.CopyToAsync(dst);

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> VerifyChecksumAsync(UpdateCheckResult result, string archivePath)
    {
        if (string.IsNullOrWhiteSpace(result.ChecksumUrl))
        {
            AnsiConsole.MarkupLine("[yellow]Sağlama dosyası yok, doğrulama atlandı.[/]");
            return true;
        }

        try
        {
            using var client = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(30)
            };
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "migurdex");

            var     text     = await client.GetStringAsync(result.ChecksumUrl);
            string? expected = null;
            foreach (var line in text.Split('\n'))
            {
                var parts = line.Split((char[]) [' ', '\t', '*'], StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2
                    && parts[^1].Trim().Equals(result.AssetName, StringComparison.OrdinalIgnoreCase))
                {
                    expected = parts[0].Trim();
                    break;
                }
            }

            if (string.IsNullOrWhiteSpace(expected))
            {
                AnsiConsole.MarkupLine("[yellow]Paket sağlama listesinde yok, doğrulama atlandı.[/]");
                return true;
            }

            await using var fs     = File.OpenRead(archivePath);
            var             actual = Convert.ToHexString(await SHA256.HashDataAsync(fs));

            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            {
                AnsiConsole.MarkupLine("[red]Sağlama uyuşmadı, güncelleme iptal edildi![/]");
                return false;
            }

            AnsiConsole.MarkupLine("[green]Sağlama doğrulandı.[/]");
            return true;
        }
        catch
        {
            AnsiConsole.MarkupLine("[yellow]Sağlama indirilemedi, doğrulama atlandı.[/]");
            return true;
        }
    }

    private static void ExtractArchive(string archivePath, string destDir)
    {
        if (archivePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            ZipFile.ExtractToDirectory(archivePath, destDir, true);
            return;
        }

        using var fs = File.OpenRead(archivePath);
        using var gz = new GZipStream(fs, CompressionMode.Decompress);
        TarFile.ExtractToDirectory(gz, destDir, true);
    }

    private static string FindPayloadRoot(string extractDir)
    {
        var entries = Directory.GetFileSystemEntries(extractDir);
        if (entries.Length == 1 && Directory.Exists(entries[0]))
        {
            var inner = Directory.GetFileSystemEntries(entries[0]);
            if (inner.Any(e => Path.GetFileName(e).Equals("api", StringComparison.OrdinalIgnoreCase)))
            {
                return entries[0];
            }
        }

        return extractDir;
    }

    private static (int Copied, List<string> Deferred, List<string> BackedUp) CopyTree(string src, string dst)
    {
        var deferred    = new List<string>();
        var backedUp    = new List<string>();
        var copied      = 0;
        var currentExe  = Environment.ProcessPath ?? string.Empty;
        var currentName = Path.GetFileName(currentExe);

        foreach (var file in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
        {
            var rel  = Path.GetRelativePath(src, file);
            var dest = Path.Combine(dst, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);

            try
            {
                File.Copy(file, dest, true);
                copied++;
            }
            catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException)
                                       && !string.IsNullOrEmpty(currentName)
                                       && Path.GetFileName(dest)
                                              .Equals(currentName, StringComparison.OrdinalIgnoreCase)
                                       && TrySwapLockedExe(file, dest))
            {
                copied++;
                backedUp.Add(rel + ".old");
            }
            catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException)
                                       && !string.IsNullOrEmpty(currentName)
                                       && Path.GetFileName(dest)
                                              .Equals(currentName, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    File.Copy(file, dest + ".new", true);
                    deferred.Add(rel);
                }
                catch
                {
                    // ignored
                }
            }
            catch
            {
                // ignored
            }
        }

        MakeExecutable(dst, "migurdex");
        MakeExecutable(Path.Combine(dst, "api"), OperatingSystem.IsWindows() ? "Migurdex.Api.exe" : "Migurdex.Api");

        return (copied, deferred, backedUp);
    }

    private static bool TrySwapLockedExe(string newFile, string dest)
    {
        try
        {
            var backup = dest + ".old";
            try
            {
                if (File.Exists(backup))
                {
                    File.Delete(backup);
                }
            }
            catch
            {
                // ignored
            }

            File.Move(dest, backup);
            File.Copy(newFile, dest, true);

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void MakeExecutable(string dir, string name)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            var path = Path.Combine(dir, name);
            if (File.Exists(path))
            {
                File.SetUnixFileMode(path,
                                     UnixFileMode.UserRead
                                     | UnixFileMode.UserWrite
                                     | UnixFileMode.UserExecute
                                     | UnixFileMode.GroupRead
                                     | UnixFileMode.GroupExecute
                                     | UnixFileMode.OtherRead
                                     | UnixFileMode.OtherExecute);
            }
        }
        catch
        {
            // ignored
        }
    }

    public static void RestartIntoNewVersion(string[]? extraArgs = null)
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exe))
        {
            return;
        }

        Console.Write("\x1b[?25h");

        var isDotnetHost = Path.GetFileNameWithoutExtension(exe)
                               .Equals("dotnet", StringComparison.OrdinalIgnoreCase);

        string targetFile;
        var    targetArgs = new List<string>();

        if (isDotnetHost)
        {
            targetFile = exe;
            var dllPath = Path.Combine(AppContext.BaseDirectory, "migurdex.dll");
            if (!File.Exists(dllPath))
            {
                return;
            }

            targetArgs.Add(dllPath);
        }
        else
        {
            targetFile = exe;
        }

        targetArgs.Add("--no-update-check");
        if (extraArgs is { Length: > 0 })
        {
            targetArgs.AddRange(extraArgs);
        }

        if (!OperatingSystem.IsWindows())
        {
            try
            {
                var argv = new List<string>
                {
                    targetFile
                };
                argv.AddRange(targetArgs);

                var ptrs = new IntPtr[argv.Count + 1];
                for (var i = 0; i < argv.Count; i++)
                {
                    ptrs[i] = Marshal.StringToHGlobalAnsi(argv[i]);
                }

                ptrs[argv.Count] = IntPtr.Zero;

                execvp(targetFile, ptrs);

                for (var i = 0; i < argv.Count; i++)
                {
                    Marshal.FreeHGlobal(ptrs[i]);
                }
            }
            catch
            {
                // fallback to Process.Start
            }
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName         = targetFile,
                UseShellExecute  = false,
                CreateNoWindow   = false,
                WorkingDirectory = Environment.CurrentDirectory
            };

            foreach (var arg in targetArgs)
            {
                psi.ArgumentList.Add(arg);
            }

            var proc = Process.Start(psi);
            if (proc != null)
            {
                proc.WaitForExit();
                Environment.Exit(proc.ExitCode);
            }
        }
        catch
        {
            // ignored
        }
    }

    private static void Help()
    {
        AnsiConsole.WriteLine("Kullanım: migurdex update [--check] [--channel stable|prerelease] [-y] [--no-restart]");
        AnsiConsole.WriteLine("  (bayraksız)    Yeni sürüm varsa indirip kurar ve yeniden başlatır (onaylı).");
        AnsiConsole.WriteLine("  --check        Sadece kontrol eder, kurmaz.");
        AnsiConsole.WriteLine("  --channel      Bu seferlik kanal seçer, ayarı değiştirmez.");
        AnsiConsole.WriteLine("  -y, --yes      Onay sormadan kurar.");
        AnsiConsole.WriteLine("  --no-restart   Güncelleme sonrası otomatik yeniden başlatmayı atlar.");
    }
}

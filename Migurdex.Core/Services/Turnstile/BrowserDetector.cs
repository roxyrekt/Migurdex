using Microsoft.Win32;
using Migurdex.Shared.Models;
using System.Runtime.Versioning;

namespace Migurdex.Core.Services.Turnstile;

public static class BrowserDetector
{
    private static readonly (string Binary, string Name, BrowserKind Kind)[] _linuxCandidates =
    [
        ("google-chrome-stable", "Chrome", BrowserKind.Chromium),
        ("google-chrome", "Chrome", BrowserKind.Chromium),
        ("chromium", "Chromium", BrowserKind.Chromium),
        ("chromium-browser", "Chromium", BrowserKind.Chromium),
        ("brave-browser", "Brave", BrowserKind.Chromium),
        ("brave", "Brave", BrowserKind.Chromium),
        ("microsoft-edge-stable", "Edge", BrowserKind.Chromium),
        ("microsoft-edge", "Edge", BrowserKind.Chromium),
        ("helium-browser", "Helium", BrowserKind.Chromium),
        ("helium", "Helium", BrowserKind.Chromium),
        ("vivaldi", "Vivaldi", BrowserKind.Chromium),
        ("opera", "Opera", BrowserKind.Chromium),
        ("zen-browser", "Zen", BrowserKind.Firefox),
        ("zen", "Zen", BrowserKind.Firefox),
        ("firefox", "Firefox", BrowserKind.Firefox),
        ("firefox-esr", "Firefox ESR", BrowserKind.Firefox),
    ];

    private static readonly string[] _linuxFixedPaths =
    [
        "/opt/google/chrome/chrome",
        "/opt/chromium/chrome",
        "/opt/brave.com/brave/brave",
        "/opt/microsoft/msedge/msedge",
        "/opt/helium-browser-bin/helium",
        "/opt/helium-browser-bin/helium-wrapper",
        "/opt/zen-browser-bin/zen",
        "/opt/zen-browser-bin/zen-bin",
        "/opt/vivaldi/vivaldi",
        "/opt/opera/opera",
        "/opt/firefox/firefox",
        "/snap/bin/chromium",
        "/snap/bin/firefox",
        "/snap/bin/brave",
    ];

    private static readonly string[] _flatpakExportDirs =
    [
        "/var/lib/flatpak/exports/bin",
        "/var/run/flatpak/exports/bin",
    ];

    private static readonly (string AppId, string Name, BrowserKind Kind)[] _flatpakApps =
    [
        ("org.chromium.Chromium", "Chromium (flatpak)", BrowserKind.Chromium),
        ("com.google.Chrome", "Chrome (flatpak)", BrowserKind.Chromium),
        ("com.brave.Browser", "Brave (flatpak)", BrowserKind.Chromium),
        ("com.microsoft.Edge", "Edge (flatpak)", BrowserKind.Chromium),
        ("net.imput.Helium", "Helium (flatpak)", BrowserKind.Chromium),
        ("app.zen_browser.zen", "Zen (flatpak)", BrowserKind.Firefox),
        ("org.mozilla.firefox", "Firefox (flatpak)", BrowserKind.Firefox),
    ];

    public static IReadOnlyList<BrowserInfo> Detect()
    {
        return OperatingSystem.IsWindows() ? DetectWindows() : DetectLinux();
    }

    public static IReadOnlyList<string> DescribeSearch()
    {
        if (OperatingSystem.IsWindows())
        {
            return
            [
                @"HKLM/HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{chrome.exe,msedge.exe,brave.exe,firefox.exe}",
                @"%PROGRAMFILES%\Google\Chrome\Application\chrome.exe",
                @"%LOCALAPPDATA%\Google\Chrome\Application\chrome.exe",
                @"%PROGRAMFILES(X86)%\Microsoft\Edge\Application\msedge.exe",
                @"%PROGRAMFILES%\BraveSoftware\Brave-Browser\Application\brave.exe",
                @"%PROGRAMFILES%\Mozilla Firefox\firefox.exe",
            ];
        }

        var pathDirs = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        var searched = new List<string>();
        foreach (var dir in pathDirs)
        {
            foreach (var (binary, _, _) in _linuxCandidates)
            {
                searched.Add(Path.Combine(dir, binary));
            }
        }

        searched.AddRange(_linuxFixedPaths);
        searched.AddRange(_flatpakExportDirs.SelectMany(dir => _flatpakApps.Select(a => Path.Combine(dir, a.AppId))));
        return searched;
    }

    private static IReadOnlyList<BrowserInfo> DetectLinux()
    {
        var found         = new List<BrowserInfo>();
        var seen          = new HashSet<string>(StringComparer.Ordinal);
        var seenCanonical = new HashSet<string>(StringComparer.Ordinal);

        string GetCanonical(string path)
        {
            try
            {
                var link = File.ResolveLinkTarget(path, returnFinalTarget: true);
                return link?.FullName ?? Path.GetFullPath(path);
            }
            catch
            {
                return path;
            }
        }

        var pathDirs = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

        foreach (var (binary, name, kind) in _linuxCandidates)
        {
            foreach (var dir in pathDirs)
            {
                string candidate;
                try { candidate = Path.Combine(dir, binary); }
                catch { continue; }

                if (IsExecutable(candidate))
                {
                    var canonical = GetCanonical(candidate);
                    if (seenCanonical.Add(canonical) && seen.Add(candidate))
                    {
                        found.Add(new BrowserInfo
                        {
                            Name           = name,
                            ExecutablePath = candidate,
                            Kind           = kind
                        });
                        break;
                    }
                }
            }
        }

        foreach (var fixedPath in _linuxFixedPaths)
        {
            if (IsExecutable(fixedPath))
            {
                var canonical = GetCanonical(fixedPath);
                if (seenCanonical.Add(canonical) && seen.Add(fixedPath))
                {
                    var isFirefox = fixedPath.Contains("firefox", StringComparison.OrdinalIgnoreCase)
                                    || fixedPath.Contains("zen", StringComparison.OrdinalIgnoreCase);
                    found.Add(new BrowserInfo
                    {
                        Name           = GuessLinuxFixedName(fixedPath),
                        ExecutablePath = fixedPath,
                        Kind           = isFirefox ? BrowserKind.Firefox : BrowserKind.Chromium
                    });
                }
            }
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var userExport = string.IsNullOrWhiteSpace(home)
                             ? string.Empty
                             : Path.Combine(home, ".local", "share", "flatpak", "exports", "bin");
        foreach (var dir in _flatpakExportDirs.Append(userExport).Where(d => !string.IsNullOrWhiteSpace(d)))
        {
            foreach (var (appId, name, kind) in _flatpakApps)
            {
                var wrapper = Path.Combine(dir, appId);
                if (IsExecutable(wrapper) && seen.Add(wrapper))
                {
                    found.Add(new BrowserInfo
                    {
                        Name           = name,
                        ExecutablePath = wrapper,
                        Kind           = kind
                    });
                }
            }
        }

        return found
               .OrderBy(b => b.Kind == BrowserKind.Chromium ? 0 : 1)
               .ThenBy(b => b.Name, StringComparer.Ordinal)
               .ToList();
    }

    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<BrowserInfo> DetectWindows()
    {
        var found = new List<BrowserInfo>();
        var seen  = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        string[] exes =
        [
            "chrome.exe", "msedge.exe", "brave.exe", "helium.exe", "vivaldi.exe", "opera.exe", "zen.exe", "firefox.exe"
        ];
        foreach (var exe in exes)
        {
            foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
            {
                foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                {
                    try
                    {
                        using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                        using var key =
                            baseKey.OpenSubKey(@$"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{exe}");
                        var value = key?.GetValue(null) as string;
                        if (!string.IsNullOrWhiteSpace(value) && File.Exists(value) && seen.Add(value))
                        {
                            found.Add(new BrowserInfo
                            {
                                Name           = GuessWindowsName(exe),
                                ExecutablePath = value,
                                Kind = exe.Equals("firefox.exe", StringComparison.OrdinalIgnoreCase)
                                       || exe.Equals("zen.exe", StringComparison.OrdinalIgnoreCase)
                                           ? BrowserKind.Firefox
                                           : BrowserKind.Chromium
                            });
                        }
                    }
                    catch
                    {
                        // ignored
                    }
                }
            }
        }

        var programFiles    = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var localAppData    = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string[] fixedExes =
        [
            Path.Combine(programFiles, @"Google\Chrome\Application\chrome.exe"),
            Path.Combine(localAppData, @"Google\Chrome\Application\chrome.exe"),
            Path.Combine(programFiles, @"Microsoft\Edge\Application\msedge.exe"),
            Path.Combine(programFilesX86, @"Microsoft\Edge\Application\msedge.exe"),
            Path.Combine(programFiles, @"BraveSoftware\Brave-Browser\Application\brave.exe"),
            Path.Combine(localAppData, @"BraveSoftware\Brave-Browser\Application\brave.exe"),
            Path.Combine(programFiles, @"Mozilla Firefox\firefox.exe"),
        ];
        foreach (var fixedExe in fixedExes)
        {
            try
            {
                if (File.Exists(fixedExe) && seen.Add(fixedExe))
                {
                    found.Add(new BrowserInfo
                    {
                        Name           = GuessWindowsName(Path.GetFileName(fixedExe)),
                        ExecutablePath = fixedExe,
                        Kind = fixedExe.Contains("firefox", StringComparison.OrdinalIgnoreCase)
                                   ? BrowserKind.Firefox
                                   : BrowserKind.Chromium
                    });
                }
            }
            catch
            {
                // ignored
            }
        }

        return found
               .OrderBy(b => b.Kind == BrowserKind.Chromium ? 0 : 1)
               .ThenBy(b => b.Name, StringComparer.Ordinal)
               .ToList();
    }

    private static string GuessLinuxFixedName(string path)
    {
        var lower = path.ToLowerInvariant();
        if (lower.Contains("helium"))
        {
            return "Helium";
        }

        if (lower.Contains("zen"))
        {
            return "Zen";
        }

        if (lower.Contains("chrome"))
        {
            return "Chrome";
        }

        if (lower.Contains("brave"))
        {
            return "Brave";
        }

        if (lower.Contains("edge") || lower.Contains("msedge"))
        {
            return "Edge";
        }

        if (lower.Contains("vivaldi"))
        {
            return "Vivaldi";
        }

        if (lower.Contains("opera"))
        {
            return "Opera";
        }

        if (lower.Contains("firefox"))
        {
            return "Firefox";
        }

        return Path.GetFileName(path);
    }

    private static string GuessWindowsName(string exe)
    {
        return exe.ToLowerInvariant() switch
        {
            "chrome.exe"  => "Chrome",
            "msedge.exe"  => "Edge",
            "brave.exe"   => "Brave",
            "helium.exe"  => "Helium",
            "vivaldi.exe" => "Vivaldi",
            "opera.exe"   => "Opera",
            "zen.exe"     => "Zen",
            "firefox.exe" => "Firefox",
            _             => exe
        };
    }

    private static bool IsExecutable(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            if (OperatingSystem.IsWindows())
            {
                return true;
            }

            return (File.GetUnixFileMode(path)
                        & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
        }
        catch
        {
            return false;
        }
    }
}

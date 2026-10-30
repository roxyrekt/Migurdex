using Migurdex.Cli.Services.Downloads;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;

namespace Migurdex.Cli.Dependencies;

public sealed class DependencyResolver
{
    private static readonly TimeSpan VersionTimeout = TimeSpan.FromSeconds(3);

    private readonly Func<DependencyKind, string?>        _configuredPath;
    private readonly IReadOnlyList<string>                _extraSearchDirectories;
    private readonly object                               _gate = new();
    private readonly IDependencyInstaller                 _installer;
    private readonly Func<string?>                        _pathEnvironment;
    private readonly IExternalProcessRunner               _processRunner;
    private readonly Dictionary<DependencyKind, string?>  _resolved = new();
    private readonly string                               _toolsDirectory;

    public DependencyResolver(
        IDependencyInstaller          installer,
        Func<DependencyKind, string?> configuredPath,
        string                        toolsDirectory,
        Func<string?>?                pathEnvironment       = null,
        IExternalProcessRunner?       processRunner         = null,
        IEnumerable<string>?          extraSearchDirectories = null)
    {
        _installer              = installer ?? throw new ArgumentNullException(nameof(installer));
        _configuredPath         = configuredPath ?? throw new ArgumentNullException(nameof(configuredPath));
        _toolsDirectory         = toolsDirectory ?? throw new ArgumentNullException(nameof(toolsDirectory));
        _pathEnvironment        = pathEnvironment ?? (() => Environment.GetEnvironmentVariable("PATH"));
        _processRunner          = processRunner ?? new ExternalProcessRunner();
        _extraSearchDirectories = (extraSearchDirectories ?? []).Where(d => !string.IsNullOrWhiteSpace(d)).ToList();
    }

    public IDependencyInstaller Installer      => _installer;
    public string               PlatformName   => _installer.PlatformName;
    public bool                 CanAutoInstall => _installer.CanAutoInstall;
    public string               ToolsDirectory => _toolsDirectory;

    public void InvalidateCache()
    {
        lock (_gate)
        {
            _resolved.Clear();
        }
    }

    public string? Resolve(DependencyKind kind)
    {
        lock (_gate)
        {
            if (_resolved.TryGetValue(kind, out var cached))
            {
                return cached;
            }

            var path = ResolveCore(kind);
            _resolved[kind] = path;

            return path;
        }
    }

    public async Task<DependencyStatus> CheckAsync(
        DependencyKind    kind,
        bool              includeVersion    = false,
        CancellationToken cancellationToken = default)
    {
        var path = Resolve(kind);
        if (path is null)
        {
            return new DependencyStatus(kind, DependencyState.Missing, null, null);
        }

        string? version = null;
        if (includeVersion)
        {
            version = await TryGetVersionAsync(kind, path, cancellationToken).ConfigureAwait(false);
        }

        return new DependencyStatus(kind, DependencyState.Installed, path, version);
    }

    private string? ResolveCore(DependencyKind kind)
    {
        var configured = _configuredPath(kind)?.Trim();

        if (!string.IsNullOrEmpty(configured) && LooksLikePath(configured))
        {
            return File.Exists(configured) ? Path.GetFullPath(configured) : null;
        }

        var inToolsDirectory = FindInDirectory(_toolsDirectory, kind);
        if (inToolsDirectory is not null)
        {
            return inToolsDirectory;
        }

        foreach (var extraDirectory in _extraSearchDirectories)
        {
            var inExtraDirectory = FindInDirectory(extraDirectory, kind);
            if (inExtraDirectory is not null)
            {
                return inExtraDirectory;
            }
        }

        var commandName = string.IsNullOrEmpty(configured) ? DependencyCatalog.CommandName(kind) : configured;

        return FindOnPath(commandName, kind)
               ?? FindOnPath(DependencyCatalog.CommandName(kind), kind);
    }

    private static bool LooksLikePath(string value)
    {
        return value.Contains(Path.DirectorySeparatorChar)
               || value.Contains(Path.AltDirectorySeparatorChar)
               || Path.IsPathRooted(value)
               || File.Exists(value);
    }

    private static string? FindInDirectory(string directory, DependencyKind kind)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return null;
        }

        foreach (var candidate in Candidates(kind))
        {
            try
            {
                var fullPath = Path.Combine(directory, candidate);
                if (File.Exists(fullPath))
                {
                    return fullPath;
                }
            }
            catch (ArgumentException)
            {
            }
        }

        return null;
    }

    private string? FindOnPath(string commandName, DependencyKind kind)
    {
        if (string.IsNullOrWhiteSpace(commandName))
        {
            return null;
        }

        var pathValue = _pathEnvironment();
        if (string.IsNullOrWhiteSpace(pathValue))
        {
            return null;
        }

        var candidates = new List<string> { commandName };
        foreach (var candidate in Candidates(kind))
        {
            if (!candidates.Contains(candidate, StringComparer.OrdinalIgnoreCase))
            {
                candidates.Add(candidate);
            }
        }

        foreach (var directory in pathValue.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                continue;
            }

            foreach (var candidate in candidates)
            {
                try
                {
                    var fullPath = Path.Combine(directory.Trim(), candidate);
                    if (File.Exists(fullPath))
                    {
                        return fullPath;
                    }
                }
                catch (ArgumentException)
                {
                }
            }
        }

        return null;
    }

    private static IEnumerable<string> Candidates(DependencyKind kind)
    {
        var candidates = new List<string> { DependencyCatalog.FileName(kind) };
        foreach (var candidate in DependencyCatalog.ExecutableCandidates(kind))
        {
            if (!candidates.Contains(candidate, StringComparer.OrdinalIgnoreCase))
            {
                candidates.Add(candidate);
            }
        }

        return candidates;
    }

    private async Task<string?> TryGetVersionAsync(
        DependencyKind     kind,
        string             path,
        CancellationToken  cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName               = path,
            UseShellExecute        = false,
            CreateNoWindow         = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true
        };
        startInfo.ArgumentList.Add(DependencyCatalog.VersionArgument(kind));

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(VersionTimeout);
            var result = await _processRunner.RunAsync(startInfo, cts.Token).ConfigureAwait(false);
            if (result.ExitCode != 0)
            {
                return null;
            }

            var firstLine = (result.StandardOutput + "\n" + result.StandardError)
                           .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                           .FirstOrDefault();

            return Truncate(firstLine);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (ExternalProcessStartException)
        {
            return null;
        }
        catch (Win32Exception)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static string? Truncate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        const int maxLength = 80;

        return value.Length <= maxLength
                   ? value
                   : string.Create(CultureInfo.InvariantCulture, $"{value[..(maxLength - 1)]}…");
    }
}

using Migurdex.Shared.Models;
using System.Text.Json;

namespace Migurdex.Core.Services;

public sealed class OAuthTokenStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true
    };

    private readonly string                        _filePath;
    private readonly Lock                          _lock   = new();
    private          Dictionary<string, OAuthToken> _tokens = new(StringComparer.OrdinalIgnoreCase);

    public OAuthTokenStore(string? configDirectory = null)
    {
        var dir = configDirectory
                  ?? Path.Combine(
                      Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                      ".config",
                      "migurdex");
        Directory.CreateDirectory(dir);
        _filePath = Path.Combine(dir, "tokens.json");
        Load();
    }

    public bool TryGet(string provider, out OAuthToken? token)
    {
        lock (_lock)
        {
            return _tokens.TryGetValue(Normalize(provider), out token);
        }
    }

    public void Set(OAuthToken token)
    {
        lock (_lock)
        {
            _tokens[Normalize(token.Provider)] = token;
            Save();
        }
    }

    public bool Remove(string provider)
    {
        lock (_lock)
        {
            if (!_tokens.Remove(Normalize(provider)))
            {
                return false;
            }

            Save();
            return true;
        }
    }

    public IReadOnlyList<string> Providers
    {
        get
        {
            lock (_lock)
            {
                return [.. _tokens.Keys];
            }
        }
    }

    private static string Normalize(string provider)
    {
        return provider.Trim().ToLowerInvariant();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return;
            }

            var json   = File.ReadAllText(_filePath);
            var loaded = JsonSerializer.Deserialize<Dictionary<string, OAuthToken>>(json, JsonOpts);
            if (loaded is not null)
            {
                _tokens = new Dictionary<string, OAuthToken>(loaded, StringComparer.OrdinalIgnoreCase);
            }
        }
        catch
        {
            try
            {
                File.Copy(_filePath,
                          $"{_filePath}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}.bak",
                          false);
            }
            catch
            {
                // ignored
            }

            _tokens = new Dictionary<string, OAuthToken>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void Save()
    {
        try
        {
            var tmp = _filePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_tokens, JsonOpts));
            RestrictPermissions(tmp);
            File.Move(tmp, _filePath, true);
            RestrictPermissions(_filePath);
        }
        catch
        {
            // ignored
        }
    }

    private static void RestrictPermissions(string path)
    {
        try
        {
            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                File.SetUnixFileMode(path,
                                     UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
        catch
        {
            // ignored
        }
    }
}

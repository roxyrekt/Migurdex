using System.Text.Json;

namespace Migurdex.Cli.Dependencies;

public sealed class DependencyVersionStore
{
    public const string MarkerPrefix = ".migurdex-version-";

    private readonly string _toolsDirectory;

    public DependencyVersionStore(string toolsDirectory)
    {
        _toolsDirectory = toolsDirectory ?? throw new ArgumentNullException(nameof(toolsDirectory));
    }

    public string MarkerPath(DependencyKind kind)
    {
        var subdirectory = DependencyCatalog.ToolSubdirectory(kind);
        var directory    = subdirectory is null ? _toolsDirectory : Path.Combine(_toolsDirectory, subdirectory);

        return Path.Combine(directory, MarkerPrefix + DependencyCatalog.CommandName(kind));
    }

    public string? Read(DependencyKind kind)
    {
        try
        {
            var path = MarkerPath(kind);
            if (!File.Exists(path))
            {
                return null;
            }

            var text = File.ReadAllText(path).Trim();

            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Write(DependencyKind kind, string tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return;
        }

        try
        {
            var path = MarkerPath(kind);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, tag.Trim());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    public void Clear(DependencyKind kind)
    {
        try
        {
            var path = MarkerPath(kind);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

public sealed class DependencyUpdateCache
{
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true
    };

    public DateTimeOffset             CheckedAt { get; set; }
    public Dictionary<string, string> Latest    { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public string? Get(DependencyKind kind)
    {
        return Latest.TryGetValue(DependencyCatalog.CommandName(kind), out var tag) ? tag : null;
    }

    public void Set(DependencyKind kind, string tag)
    {
        Latest[DependencyCatalog.CommandName(kind)] = tag;
    }

    public static DependencyUpdateCache Load(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return new DependencyUpdateCache();
            }

            return JsonSerializer.Deserialize<DependencyUpdateCache>(File.ReadAllText(path), _jsonOptions)
                   ?? new DependencyUpdateCache();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new DependencyUpdateCache();
        }
    }

    public void Save(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, _jsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

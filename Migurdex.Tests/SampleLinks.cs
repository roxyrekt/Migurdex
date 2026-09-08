using System.Text.Json;

namespace Migurdex.Tests;

public static class SampleLinks
{
    public static Dictionary<string, string[]> Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "extractor-smoke-cases.json");

        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"sample links file not found: {path}");
        }

        var json = File.ReadAllText(path);

        return JsonSerializer.Deserialize<Dictionary<string, string[]>>(json)
               ?? throw new InvalidOperationException($"could not parse sample links file: {path}");
    }
}

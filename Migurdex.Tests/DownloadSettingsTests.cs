using Migurdex.Cli.Configuration;
using System.Text.Json;
using Xunit;

namespace Migurdex.Tests;

/// <summary>
/// Ayarlar yüzeyinin kalıcılık ve sınır davranışı. config.json'a yazılan biçim
/// <see cref="CliConfig"/> üzerinden System.Text.Json ile üretildiği için burada aynı
/// serileştirme turu kullanılır: yazılan bir değer, yeni bir süreçte okunduğunda aynı olmalıdır.
/// </summary>
public sealed class DownloadSettingsTests
{
    [Fact]
    public void NewConfig_EnablesParallelDownloadWithTwoWorkersByDefault()
    {
        var config = new CliConfig();

        Assert.True(config.DownloadParallelEnabled);
        Assert.Equal(2, config.DownloadConcurrency);
        Assert.Equal(2, CliConfig.DefaultDownloadConcurrency);
        Assert.Equal(1, CliConfig.MinDownloadConcurrency);
        Assert.Equal(8, CliConfig.MaxDownloadConcurrency);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-4, 1)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(8, 8)]
    [InlineData(9, 8)]
    [InlineData(1000, 8)]
    public void Concurrency_IsClampedOnEveryWrite(int requested, int expected)
    {
        var config = new CliConfig
        {
            DownloadConcurrency = requested
        };

        Assert.Equal(expected, config.DownloadConcurrency);
        Assert.Equal(expected, CliConfig.ClampConcurrency(requested));
    }

    [Fact]
    public void OldConfigWithoutTheNewKeys_KeepsSafeDefaults()
    {
        var config = JsonSerializer.Deserialize<CliConfig>("{}");

        Assert.NotNull(config);
        Assert.True(config.DownloadParallelEnabled);
        Assert.Equal(2, config.DownloadConcurrency);
    }

    [Fact]
    public void HandEditedConfigWithOutOfRangeConcurrency_IsRepairedWhileLoading()
    {
        var config = JsonSerializer.Deserialize<CliConfig>(
            "{\"DownloadParallelEnabled\":true,\"DownloadConcurrency\":99}");

        Assert.NotNull(config);
        Assert.Equal(CliConfig.MaxDownloadConcurrency, config.DownloadConcurrency);
    }

    [Fact]
    public void DownloadDirectoryAndParallelSettings_SurviveAJsonRoundTrip()
    {
        var target = Path.Combine(Path.GetTempPath(), "migurdex-batch-target");
        var original = new CliConfig
        {
            DownloadDirectory       = target,
            DownloadParallelEnabled = false,
            DownloadConcurrency     = 5
        };

        // ConfigurationService bu biçimi (WriteIndented, varsayılan adlandırma) dosyaya yazar ve
        // yeni süreçte aynı biçimi okuyarak CliConfig üretir.
        var json = JsonSerializer.Serialize(original);
        var reloaded = JsonSerializer.Deserialize<CliConfig>(json);

        Assert.NotNull(reloaded);
        Assert.Equal(target, reloaded.DownloadDirectory);
        Assert.False(reloaded.DownloadParallelEnabled);
        Assert.Equal(5, reloaded.DownloadConcurrency);
    }

    [Fact]
    public void DownloadDirectory_EmptyValueFallsBackToPlatformDefault()
    {
        var config = new CliConfig
        {
            DownloadDirectory = "   "
        };

        Assert.Equal(CliConfig.DefaultDownloadDirectory, config.DownloadDirectory);
    }

    [Fact]
    public void DownloadDirectory_QuotesAreTrimmedOnEveryWrite()
    {
        var quoted = CliConfig.NormalizeDownloadDirectory("  \"C:\\Anime İndir\"  ");
        Assert.Equal("C:\\Anime İndir", quoted);
        Assert.Equal(quoted, new CliConfig { DownloadDirectory = "  \"C:\\Anime İndir\"  " }.DownloadDirectory);
    }

    [Fact]
    public void DownloadDirectory_EnvironmentVariablesAreExpanded()
    {
        var variableName = "MIGURDEX_TEST_ROOT";
        var previous     = Environment.GetEnvironmentVariable(variableName);
        var root         = Path.Combine(Path.GetTempPath(), "migurdex-env-root");
        Environment.SetEnvironmentVariable(variableName, root);
        try
        {
            var input = OperatingSystem.IsWindows()
                            ? $"%{variableName}%{Path.DirectorySeparatorChar}Anime"
                            : $"${variableName}{Path.DirectorySeparatorChar}Anime";
            var resolved = CliConfig.NormalizeDownloadDirectory(input);

            Assert.Equal(Path.Combine(root, "Anime"), resolved);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variableName, previous);
        }
    }

    [Fact]
    public void DownloadDirectory_ExpandsAllVariableSyntaxesOnEveryPlatform()
    {
        var variableName = "MIGURDEX_TEST_ROOT";
        var previous     = Environment.GetEnvironmentVariable(variableName);
        var root         = Path.Combine(Path.GetTempPath(), "migurdex-env-root");
        Environment.SetEnvironmentVariable(variableName, root);
        try
        {
            var suffix = $"{Path.DirectorySeparatorChar}Anime";

            Assert.Equal(Path.Combine(root, "Anime"),
                         CliConfig.NormalizeDownloadDirectory($"%{variableName}%{suffix}"));
            Assert.Equal(Path.Combine(root, "Anime"),
                         CliConfig.NormalizeDownloadDirectory($"${variableName}{suffix}"));
            Assert.Equal(Path.Combine(root, "Anime"),
                         CliConfig.NormalizeDownloadDirectory($"${{{variableName}}}{suffix}"));

            var missingVariable = "MIGURDEX_UNSET_" + Guid.NewGuid().ToString("N");
            Assert.Equal($"%{missingVariable}%{suffix}",
                         CliConfig.NormalizeDownloadDirectory($"%{missingVariable}%{suffix}"));
            Assert.Equal($"${missingVariable}{suffix}",
                         CliConfig.NormalizeDownloadDirectory($"${missingVariable}{suffix}"));
            Assert.Equal($"${{{missingVariable}}}{suffix}",
                         CliConfig.NormalizeDownloadDirectory($"${{{missingVariable}}}{suffix}"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(variableName, previous);
        }
    }

    [Fact]
    public void DownloadDirectory_TildeExpandsToTheUserProfile()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(userProfile))
        {
            return;
        }

        var resolved = CliConfig.NormalizeDownloadDirectory("~/Anime");
        Assert.StartsWith(userProfile, resolved, StringComparison.Ordinal);
        Assert.Contains("Anime", resolved, StringComparison.Ordinal);
    }

    [Fact]
    public void DefaultDownloadDirectory_IsRootedAndNamedMigurdex()
    {
        Assert.True(Path.IsPathRooted(CliConfig.DefaultDownloadDirectory));
        Assert.EndsWith("Migurdex", CliConfig.DefaultDownloadDirectory, StringComparison.Ordinal);
    }
}

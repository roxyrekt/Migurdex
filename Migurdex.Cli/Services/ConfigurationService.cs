using Migurdex.Cli.Configuration;
using System.Text.Json;

namespace Migurdex.Cli.Services;

public class ConfigurationService : IConfigurationService
{
    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        WriteIndented = true
    };

    private readonly string _configFilePath;

    public ConfigurationService()
    {
        ConfigDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".config",
            "migurdex"
        );

        Directory.CreateDirectory(ConfigDirectory);
        _configFilePath = Path.Combine(ConfigDirectory, "config.json");

        Config = Load();
    }

    public CliConfig Config          { get; private set; }
    public string    ConfigDirectory { get; }

    public void Save()
    {
        SaveConfig(Config);
    }

    public void Reload()
    {
        Config = Load();
    }

    private CliConfig Load()
    {
        if (!File.Exists(_configFilePath))
        {
            var defaultConfig = new CliConfig();
            SaveConfig(defaultConfig);
            return defaultConfig;
        }

        try
        {
            var json = File.ReadAllText(_configFilePath);
            return JsonSerializer.Deserialize<CliConfig>(json, _jsonOpts) ?? new CliConfig();
        }
        catch (Exception ex) when (ex is IOException
                                         or JsonException
                                         or NotSupportedException
                                         or UnauthorizedAccessException)
        {
            try
            {
                File.Copy(_configFilePath,
                          $"{_configFilePath}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}.bak",
                          false);
            }
            catch
            {
                // ignored
            }

            return new CliConfig();
        }
    }

    private void SaveConfig(CliConfig config)
    {
        var tmpPath = _configFilePath + ".tmp";
        File.WriteAllText(tmpPath, JsonSerializer.Serialize(config, _jsonOpts));
        File.Move(tmpPath, _configFilePath, true);
    }
}

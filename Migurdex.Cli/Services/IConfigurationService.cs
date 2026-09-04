using Migurdex.Cli.Configuration;

namespace Migurdex.Cli.Services;

public interface IConfigurationService
{
    CliConfig Config          { get; }
    string    ConfigDirectory { get; }
    void      Save();
    void      Reload();
}

using Microsoft.Extensions.DependencyInjection;
using Migurdex.Cli.Configuration;
using Migurdex.Cli.Services;

namespace Migurdex.Cli.Dependencies;

public static class DependencyServiceCollectionExtensions
{
    public static IServiceCollection AddDependencyServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton(provider =>
        {
            var configService = provider.GetRequiredService<IConfigurationService>();

            return new DependencyResolver(
                DependencyInstallers.CreateDefault(CliConfig.DefaultToolsDirectory),
                kind => kind switch
                {
                    DependencyKind.Mpv    => configService.Config.PreferredPlayer,
                    DependencyKind.YtDlp  => configService.Config.YtDlpPath,
                    DependencyKind.Ffmpeg => configService.Config.FfmpegPath,
                    _                     => null
                },
                CliConfig.DefaultToolsDirectory,
                extraSearchDirectories: BuildExtraSearchDirectories());
        });

        services.AddSingleton(provider =>
                                  new DependencyVersionStore(CliConfig.DefaultToolsDirectory));

        services.AddSingleton(provider =>
        {
            var configService = provider.GetRequiredService<IConfigurationService>();

            return new DependencyUpdateChecker(provider.GetRequiredService<DependencyResolver>(),
                                               provider.GetRequiredService<HttpClient>(),
                                               provider.GetRequiredService<DependencyVersionStore>(),
                                               Path.Combine(configService.ConfigDirectory,
                                                            "deps-check.json"));
        });

        return services;
    }

    private static IReadOnlyList<string> BuildExtraSearchDirectories()
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            return [];
        }

        var directories = new List<string>
        {
            Path.Combine(localAppData, "Microsoft", "WinGet", "Links")
        };

        var packages = Path.Combine(localAppData, "Microsoft", "WinGet", "Packages");
        if (Directory.Exists(packages))
        {
            try
            {
                directories.AddRange(
                    Directory.EnumerateDirectories(packages)
                             .SelectMany(dir =>
                             {
                                 try
                                 {
                                     return Directory.EnumerateDirectories(dir, "*", SearchOption.AllDirectories)
                                                     .Take(50);
                                 }
                                 catch (IOException)
                                 {
                                     return [];
                                 }
                                 catch (UnauthorizedAccessException)
                                 {
                                     return [];
                                 }
                             }));
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return directories.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}

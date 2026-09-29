using Microsoft.Extensions.DependencyInjection;

namespace Migurdex.Cli.Services.Downloads;

public static class DownloadServiceCollectionExtensions
{
    public static IServiceCollection AddDownloadServices(
        this IServiceCollection                              services,
        Func<IServiceProvider, HlsDownloadOptions>? hlsOptionsFactory = null)
    {
        services.AddSingleton<IDownloadPathBuilder, DownloadPathBuilder>();
        services.AddSingleton<IDownloadHttpClientFactory, DownloadHttpClientFactory>();
        services.AddSingleton<IMp4Downloader>(provider =>
            new Mp4Downloader(provider.GetRequiredService<IDownloadHttpClientFactory>()));
        services.AddSingleton<ISubtitleDownloader>(provider =>
            new SubtitleDownloader(provider.GetRequiredService<IDownloadHttpClientFactory>()));
        services.AddSingleton<IExternalProcessRunner, ExternalProcessRunner>();
        services.AddSingleton(provider => hlsOptionsFactory?.Invoke(provider) ?? new HlsDownloadOptions());
        services.AddSingleton<IHlsDownloader>(provider =>
            new YtDlpHlsDownloader(provider.GetRequiredService<IExternalProcessRunner>(),
                                  provider.GetRequiredService<HlsDownloadOptions>()));
        services.AddSingleton<IDownloadService, DownloadService>();
        return services;
    }
}

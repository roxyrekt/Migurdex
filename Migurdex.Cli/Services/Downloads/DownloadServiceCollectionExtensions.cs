using Microsoft.Extensions.DependencyInjection;
using Migurdex.Cli.Services;

namespace Migurdex.Cli.Services.Downloads;

public static class DownloadServiceCollectionExtensions
{
    public static IServiceCollection AddDownloadServices(
        this IServiceCollection                     services,
        Func<IServiceProvider, HlsDownloadOptions>? hlsOptionsFactory = null)
    {
        services.AddSingleton<IDownloadPathBuilder, DownloadPathBuilder>();
        services.AddSingleton<IDownloadHttpClientFactory, DownloadHttpClientFactory>();
        services.AddSingleton<IMp4Downloader>(provider =>
                                                  new Mp4Downloader(
                                                      provider.GetRequiredService<IDownloadHttpClientFactory>()));
        services.AddSingleton<ISubtitleDownloader>(provider =>
                                                       new SubtitleDownloader(
                                                           provider.GetRequiredService<IDownloadHttpClientFactory>()));
        services.AddSingleton<IExternalProcessRunner, ExternalProcessRunner>();
        services.AddSingleton(provider => hlsOptionsFactory?.Invoke(provider) ?? new HlsDownloadOptions());
        services.AddSingleton<IHlsDownloader>(provider =>
                                                  new YtDlpHlsDownloader(
                                                      provider.GetRequiredService<IExternalProcessRunner>(),
                                                      provider.GetRequiredService<HlsDownloadOptions>()));
        services.AddSingleton<IDownloadService>(provider => new DownloadService(
            provider.GetRequiredService<IDownloadPathBuilder>(),
            provider.GetRequiredService<IMp4Downloader>(),
            provider.GetRequiredService<IHlsDownloader>(),
            provider.GetRequiredService<ISubtitleDownloader>(),
            provider.GetRequiredService<BandwidthLimiter>()));
        services.AddSingleton<BandwidthLimiter>(provider => new BandwidthLimiter(() =>
            Math.Max(0, (long)(provider.GetRequiredService<IConfigurationService>().Config.MaxDownloadSpeedKbps * 1024))));
        services.AddSingleton<BackgroundDownloadTracker>();
        return services;
    }
}

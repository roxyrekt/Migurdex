using Microsoft.Extensions.DependencyInjection;
using Migurdex.Core.Database;

namespace Migurdex.Cli.Services.Downloads;

public static class DownloadServiceCollectionExtensions
{
    public static IServiceCollection AddDownloadServices(
        this IServiceCollection                     services,
        Func<IServiceProvider, HlsDownloadOptions>? hlsOptionsFactory = null,
        Func<IServiceProvider, Mp4DownloadOptions>? mp4OptionsFactory = null)
    {
        services.AddSingleton<IDownloadPathBuilder, DownloadPathBuilder>();
        services.AddSingleton<IDownloadHttpClientFactory, DownloadHttpClientFactory>();
        services.AddSingleton(provider => mp4OptionsFactory?.Invoke(provider) ?? new Mp4DownloadOptions());
        services.AddSingleton<IMp4Downloader>(provider =>
                                                  new Mp4Downloader(
                                                      provider.GetRequiredService<IDownloadHttpClientFactory>(),
                                                      provider.GetRequiredService<Mp4DownloadOptions>()));
        services.AddSingleton<ISubtitleDownloader>(provider =>
                                                       new SubtitleDownloader(
                                                           provider.GetRequiredService<IDownloadHttpClientFactory>()));
        services.AddSingleton<IExternalProcessRunner, ExternalProcessRunner>();
        services.AddSingleton(provider => hlsOptionsFactory?.Invoke(provider) ?? new HlsDownloadOptions());
        services.AddSingleton<IHlsDownloader>(provider =>
                                                  new YtDlpHlsDownloader(
                                                      provider.GetRequiredService<IExternalProcessRunner>(),
                                                      provider.GetRequiredService<HlsDownloadOptions>()));
        services.AddSingleton<IDownloadService, DownloadService>();
        services.AddSingleton<IDownloadQueueService>(provider =>
                                                         new DownloadQueueService(
                                                             provider.GetRequiredService<MigurdexDatabase>(),
                                                             provider.GetRequiredService<IDownloadService>(),
                                                             provider.GetRequiredService<IConfigurationService>(),
                                                             provider.GetService<IApiClientService>(),
                                                             provider.GetRequiredService<IDownloadPathBuilder>()));
        return services;
    }
}

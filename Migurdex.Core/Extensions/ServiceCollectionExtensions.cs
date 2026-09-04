using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Migurdex.Core.Extractors;
using Migurdex.Core.PluginSystem;
using Migurdex.Shared.Interfaces;

namespace Migurdex.Core.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddCoreExtractors(this IServiceCollection services)
    {
        services.AddSingleton<M3U8PlaylistExtractor>();

        services.AddSingleton<IExtractorManager>(sp =>
        {
            var loader  = sp.GetRequiredService<PluginLoader>();
            var logger  = sp.GetRequiredService<ILogger<ExtractorManager>>();
            var manager = new ExtractorManager(logger, loader);

            var m3U8 = sp.GetRequiredService<M3U8PlaylistExtractor>();
            manager.RegisterExtractor(m3U8);

            var extractorTypes = typeof(M3U8PlaylistExtractor).Assembly
                                                              .GetTypes()
                                                              .Where(t => typeof(IExtractor).IsAssignableFrom(t)
                                                                          && t is { IsClass: true, IsAbstract: false }
                                                                          && t != typeof(M3U8PlaylistExtractor));

            foreach (var type in extractorTypes)
            {
                try
                {
                    var extractor = (IExtractor) ActivatorUtilities.CreateInstance(sp, type);
                    manager.RegisterExtractor(extractor);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "failed to instantiate built-in extractor: {ExtractorType}", type.Name);
                }
            }

            return manager;
        });

        return services;
    }
}

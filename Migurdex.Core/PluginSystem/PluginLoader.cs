using Microsoft.Extensions.Logging;
using Migurdex.Shared.Interfaces;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.Loader;

namespace Migurdex.Core.PluginSystem;

public class PluginLoader(
    ISharedBridge         bridge,
    ILogger<PluginLoader> logger,
    ILoggerFactory        loggerFactory)
{
    private readonly ConcurrentDictionary<string, AssemblyLoadContext> _contexts =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<string, List<IExtractor>> _extractors = new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<string, List<IProvider>> _providers = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<IProvider>  Providers  => _providers.Values.SelectMany(x => x).ToList();
    public IReadOnlyList<IExtractor> Extractors => _extractors.Values.SelectMany(x => x).ToList();

    public void LoadPlugins(string pluginsPath)
    {
        if (!Directory.Exists(pluginsPath))
        {
            Directory.CreateDirectory(pluginsPath);

            return;
        }

        var dlls = Directory.GetFiles(pluginsPath, "*.dll");
        foreach (var dll in dlls)
        {
            try
            {
                LoadPlugin(dll);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "error loading plugin {PluginDll}", Path.GetFileName(dll));
            }
        }
    }

    public void UnloadPlugin(string dllPath)
    {
        var key = Path.GetFileName(dllPath);
        if (_contexts.TryRemove(key, out var alc))
        {
            _providers.TryRemove(key, out _);
            _extractors.TryRemove(key, out _);

            try
            {
                alc.Unload();
                logger.LogInformation("unloaded plugin: {Plugin}", key);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "error unloading plugin: {Plugin}", key);
            }
        }
    }

    public void LoadPlugin(string dllPath)
    {
        var key = Path.GetFileName(dllPath);

        UnloadPlugin(dllPath);

        Assembly?            assembly = null;
        AssemblyLoadContext? alc      = null;
        var                  retries  = 5;

        while (retries > 0)
        {
            try
            {
                alc = new AssemblyLoadContext(key, true);

                alc.Resolving += (context, assemblyName) =>
                {
                    var assemblyPath = Path.Combine(Path.GetDirectoryName(dllPath)!, assemblyName.Name + ".dll");
                    if (File.Exists(assemblyPath))
                    {
                        try
                        {
                            using var fsDep =
                                new FileStream(assemblyPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

                            return context.LoadFromStream(fsDep);
                        }
                        catch
                        {
                            return null;
                        }
                    }

                    return null;
                };

                using var fs = new FileStream(dllPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                assembly = alc.LoadFromStream(fs);

                break;
            }
            catch (IOException)
            {
                alc?.Unload();
                retries--;

                if (retries == 0)
                {
                    logger.LogError("plugin file could not be loaded because it is locked: {Plugin}", key);

                    throw;
                }

                Thread.Sleep(300);
            }
            catch (Exception ex)
            {
                alc?.Unload();
                logger.LogError(ex, "an unexpected error occurred while loading the plugin: {Plugin}", key);

                throw;
            }
        }

        if (assembly == null || alc == null)
        {
            return;
        }

        try
        {
            var providersList  = new List<IProvider>();
            var extractorsList = new List<IExtractor>();

            // load providers
            var providerTypes = assembly.GetTypes()
                                        .Where(t => typeof(IProvider).IsAssignableFrom(t)
                                                    && t is { IsInterface: false, IsAbstract: false });

            foreach (var type in providerTypes)
            {
                IProvider? provider = null;
                try
                {
                    var genericLoggerType = typeof(Logger<>).MakeGenericType(type);
                    var pluginLogger      = Activator.CreateInstance(genericLoggerType, loggerFactory);

                    if (pluginLogger is null)
                    {
                        throw new NullReferenceException("pluginLogger is null");
                    }

                    var availableServices = new[] { bridge, bridge.MetadataReader, pluginLogger };
                    if (CreateInstanceWithBestConstructor(type, availableServices) is IProvider prov)
                    {
                        provider = prov;
                    }
                }
                catch
                {
                    // ignored
                }

                if (provider == null)
                {
                    continue;
                }

                providersList.Add(provider);
                logger.LogInformation("loaded plugin: {PluginName} ({PluginType})", provider.Name, provider.Type);
            }

            // load extractors
            var extractorTypes = assembly.GetTypes()
                                         .Where(t => typeof(IExtractor).IsAssignableFrom(t)
                                                     && t is { IsInterface: false, IsAbstract: false });

            foreach (var type in extractorTypes)
            {
                IExtractor? extractor = null;
                try
                {
                    var genericLoggerType = typeof(Logger<>).MakeGenericType(type);
                    var pluginLogger      = Activator.CreateInstance(genericLoggerType, loggerFactory);

                    if (pluginLogger is null)
                    {
                        throw new NullReferenceException("pluginLogger is null");
                    }

                    var availableServices = new[] { bridge, bridge.MetadataReader, pluginLogger };
                    if (CreateInstanceWithBestConstructor(type, availableServices) is IExtractor ext)
                    {
                        extractor = ext;
                    }
                }
                catch
                {
                    // ignored
                }

                if (extractor == null)
                {
                    continue;
                }

                extractorsList.Add(extractor);
                logger.LogInformation("loaded Extractor: {ExtractorName}", extractor.Name);
            }

            _contexts[key]   = alc;
            _providers[key]  = providersList;
            _extractors[key] = extractorsList;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "error processing types in assembly {PluginDll}", key);
            alc.Unload();
        }
    }

    private static object? CreateInstanceWithBestConstructor(Type type, object?[] availableServices)
    {
        var constructors = type.GetConstructors()
                               .OrderByDescending(c => c.GetParameters().Length);

        foreach (var ctor in constructors)
        {
            var parameters = ctor.GetParameters();
            var args       = new object?[parameters.Length];
            var isMatch    = true;

            for (var i = 0; i < parameters.Length; i++)
            {
                var paramType = parameters[i].ParameterType;
                var service = availableServices
                              .Where(s => s is not null)
                              .FirstOrDefault(paramType.IsInstanceOfType);

                if (service != null)
                {
                    args[i] = service;
                }
                else
                {
                    if (parameters[i].HasDefaultValue)
                    {
                        args[i] = parameters[i].DefaultValue;
                    }
                    else
                    {
                        isMatch = false;

                        break;
                    }
                }
            }

            if (isMatch)
            {
                try
                {
                    return ctor.Invoke(args);
                }
                catch
                {
                    // ignored
                }
            }
        }

        return null;
    }
}

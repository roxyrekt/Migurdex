using Migurdex.Core.PluginSystem;
using System.Collections.Concurrent;

namespace Migurdex.Api.Services;

public class PluginWatcherService : BackgroundService
{
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _debounceTokens =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly ILogger<PluginWatcherService> _logger;
    private readonly PluginLoader                  _pluginLoader;
    private readonly string                        _pluginsPath;
    private          FileSystemWatcher?            _watcher;

    public PluginWatcherService(PluginLoader pluginLoader, ILogger<PluginWatcherService> logger)
    {
        _pluginLoader = pluginLoader;
        _logger       = logger;
        _pluginsPath  = Path.Combine(AppContext.BaseDirectory, "Plugins");
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!Directory.Exists(_pluginsPath))
        {
            Directory.CreateDirectory(_pluginsPath);
        }

        _watcher = new FileSystemWatcher(_pluginsPath, "*.dll")
        {
            NotifyFilter        = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime,
            EnableRaisingEvents = true
        };

        _watcher.Created += OnPluginFileChanged;
        _watcher.Changed += OnPluginFileChanged;
        _watcher.Deleted += OnPluginFileDeleted;
        _watcher.Renamed += OnPluginFileRenamed;

        _logger.LogInformation("plugin watcher service started. Watching path: {PluginsPath}", _pluginsPath);

        stoppingToken.Register(() =>
        {
            _watcher.Dispose();
            foreach (var cts in _debounceTokens.Values)
            {
                cts.Cancel();
            }
        });

        return Task.CompletedTask;
    }

    private void OnPluginFileChanged(object sender, FileSystemEventArgs e)
    {
        _logger.LogDebug("plugin file change detected: {Name} ({ChangeType})", e.Name, e.ChangeType);
        DebounceReload(e.FullPath);
    }

    private void OnPluginFileDeleted(object sender, FileSystemEventArgs e)
    {
        _logger.LogInformation("plugin file deletion detected: {Name}", e.Name);

        if (_debounceTokens.TryRemove(e.FullPath, out var oldCts))
        {
            oldCts.Cancel();
            oldCts.Dispose();
        }

        try
        {
            _pluginLoader.UnloadPlugin(e.FullPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "error occurred while handling deletion of plugin {File}", e.Name);
        }
    }

    private void OnPluginFileRenamed(object sender, RenamedEventArgs e)
    {
        _logger.LogInformation("plugin file rename detected: {OldName} -> {Name}", e.OldName, e.Name);

        if (_debounceTokens.TryRemove(e.OldFullPath, out var oldCts))
        {
            oldCts.Cancel();
            oldCts.Dispose();
        }

        try
        {
            _pluginLoader.UnloadPlugin(e.OldFullPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "error unloading old path of renamed plugin: {OldFile}", e.OldName);
        }

        DebounceReload(e.FullPath);
    }

    private void DebounceReload(string dllPath)
    {
        var cts = new CancellationTokenSource();
        if (_debounceTokens.TryGetValue(dllPath, out var oldCts))
        {
            oldCts.Cancel();
            oldCts.Dispose();
        }

        _debounceTokens[dllPath] = cts;

        Task.Delay(500, cts.Token)
            .ContinueWith(t =>
                          {
                              if (t.IsCanceled)
                              {
                                  return;
                              }

                              _debounceTokens.TryRemove(dllPath, out _);
                              cts.Dispose();

                              _logger.LogInformation("reloading/Loading plugin: {Plugin}", Path.GetFileName(dllPath));
                              try
                              {
                                  _pluginLoader.LoadPlugin(dllPath);
                              }
                              catch (Exception ex)
                              {
                                  _logger.LogError(ex,
                                                   "failed to load/reload plugin: {Plugin}",
                                                   Path.GetFileName(dllPath));
                              }
                          },
                          TaskScheduler.Default);
    }

    public override void Dispose()
    {
        _watcher?.Dispose();

        foreach (var cts in _debounceTokens.Values)
        {
            cts.Dispose();
        }

        base.Dispose();
    }
}

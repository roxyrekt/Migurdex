using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Migurdex.Core.Extensions;
using Migurdex.Core.Interop;
using Migurdex.Core.PluginSystem;
using Migurdex.Core.Utils;
using Migurdex.Shared.Interfaces;
using System.Text;

namespace Migurdex.Tests;

public sealed class ExtractorFixture : IDisposable
{
    public IExtractorManager ExtractorManager { get; }

    private readonly ServiceProvider _provider;
    private          bool            _disposed;

    public ExtractorFixture()
    {
        InitializeNative();
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton<HttpClient>(_ => new HttpClient());
        services.AddSingleton<IMp4MetadataReader, Mp4MetadataReader>();
        services.AddSingleton<ISharedBridge, SharedBridge>();
        services.AddSingleton<PluginLoader>(sp => new PluginLoader(sp.GetRequiredService<ISharedBridge>(),
                                                                   sp.GetRequiredService<ILoggerFactory>()
                                                                     .CreateLogger<PluginLoader>(),
                                                                   sp.GetRequiredService<ILoggerFactory>()));
        services.AddCoreExtractors();

        _provider        = services.BuildServiceProvider();
        ExtractorManager = _provider.GetRequiredService<IExtractorManager>();
    }

    private static void InitializeNative()
    {
        if (RustBridge.IsInitialized)
        {
            return;
        }

        var libName = OperatingSystem.IsWindows() ? "migurdex_native.dll" : "libmigurdex_native.so";
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
                                                     "..",
                                                     "..",
                                                     "..",
                                                     ".."));

        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, libName),
            Path.Combine(repoRoot, "Migurdex.Api", "bin", "Debug", "net10.0", libName),
            Path.Combine(repoRoot, "Migurdex.Api", "bin", "Release", "net10.0", libName),
            Path.Combine(repoRoot, "Migurdex.Native", "target", "debug", libName),
            Path.Combine(repoRoot, "Migurdex.Native", "target", "release", libName),
        };

        var found = candidates.FirstOrDefault(File.Exists);

        if (found is null)
        {
            throw new FileNotFoundException(
                $"native library '{libName}' not found. Run ./build.sh first. Searched: {string.Join(", ", candidates)}");
        }

        RustBridge.Initialize(found);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _provider.Dispose();
    }
}

using Microsoft.AspNetCore.Diagnostics;
using Migurdex.Api.Endpoints;
using Migurdex.Api.Services;
using Migurdex.Core.Extensions;
using Migurdex.Core.Interop;
using Migurdex.Core.PluginSystem;
using Migurdex.Core.Services;
using Migurdex.Core.Utils;
using Migurdex.Shared.Interfaces;
using System.Diagnostics;
using System.Text;

var builder = WebApplication.CreateBuilder(args);

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

LoggerBuilder.ConfigureLogging(builder.Services, builder.Configuration);

builder.Services.AddOpenApi();

var libName = OperatingSystem.IsWindows() ? "migurdex_native.dll" : "libmigurdex_native.so";
var dllPath = Path.Combine(AppContext.BaseDirectory, libName);

try
{
    RustBridge.Initialize(dllPath);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Kritik: native kütüphane yüklenemedi ('{dllPath}'): {ex.GetType().Name}: {ex.Message}");
    return 1;
}

builder.Services.AddHttpClient("RustClient")
       .ConfigurePrimaryHttpMessageHandler(() => new RustHttpMessageHandler());

builder.Services.AddSingleton<HttpClient>(sp => sp.GetRequiredService<IHttpClientFactory>().CreateClient("RustClient"));

builder.Services.AddSingleton<IMp4MetadataReader, Mp4MetadataReader>();
builder.Services.AddSingleton<ISharedBridge, SharedBridge>();

builder.Services.AddSingleton<PluginLoader>(sp =>
{
    var bridge        = sp.GetRequiredService<ISharedBridge>();
    var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
    var loader        = new PluginLoader(bridge, loggerFactory.CreateLogger<PluginLoader>(), loggerFactory);
    var pluginsPath   = Path.Combine(AppContext.BaseDirectory, "Plugins");
    loader.LoadPlugins(pluginsPath);

    return loader;
});

builder.Services.AddCoreExtractors();

builder.Services.AddSingleton<MetadataManager>();
builder.Services.AddTransient<IMetadataProvider, AniListProvider>();
builder.Services.AddTransient<IMetadataProvider, JikanProvider>();

builder.Services.AddHostedService<PluginWatcherService>();

var app = builder.Build();

app.UseExceptionHandler(errorApp => errorApp.Run(async context =>
{
    var logger  = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Unhandled");
    var feature = context.Features.Get<IExceptionHandlerFeature>();
    if (feature?.Error is not null)
    {
        logger.LogError(feature.Error,
                        "unhandled exception for {Method} {Path}",
                        context.Request.Method,
                        context.Request.Path);
    }

    context.Response.StatusCode = StatusCodes.Status500InternalServerError;
    await context.Response.WriteAsJsonAsync(new
    {
        error = "Beklenmeyen sunucu hatası."
    });
}));

app.Use(async (context, next) =>
{
    var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Access");
    var sw     = Stopwatch.StartNew();
    await next();
    sw.Stop();
    if (!context.Request.Path.StartsWithSegments("/health"))
    {
        logger.LogDebug("{Method} {Path} -> {Status} ({Elapsed}ms)",
                        context.Request.Method,
                        context.Request.Path,
                        context.Response.StatusCode,
                        sw.ElapsedMilliseconds);
    }
});

app.MapGet("/health",
           (PluginLoader loader, IExtractorManager extractorManager) => Results.Ok(new
           {
               status     = "OK",
               providers  = loader.Providers.Count,
               extractors = extractorManager.Extractors.Count,
               rust       = RustBridge.IsInitialized,
               time       = DateTime.UtcNow
           }));

app.MapOpenApi();
app.MapAnimeEndpoints();
app.MapMetadataEndpoints();
app.MapExtractorEndpoints();

app.Run();

return 0;

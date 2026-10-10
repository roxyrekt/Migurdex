using Migurdex.Core.Services.Turnstile;
using Migurdex.Shared.Interfaces;
using Migurdex.Shared.Models;

namespace Migurdex.Api.Endpoints;

public sealed record SolverClearanceRequest(string Host, string Value, double? ExpiresInHours);

public static class SolverEndpoints
{
    public static IEndpointRouteBuilder MapSolverEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/solver/status",
                   (CfClearanceStore store, TurnstileService solver, IBlameCollector blame) =>
                   {
                       var browsers = BrowserDetector.Detect()
                                                     .Select(b => new
                                                     {
                                                         name = b.Name,
                                                         path = b.ExecutablePath,
                                                         kind = b.Kind.ToString()
                                                     });
                       var cached = store.GetHosts()
                                         .Select(h => new
                                         {
                                             host      = h.Host,
                                             expiresAt = h.ExpiresAtUtc,
                                             source    = h.Source
                                         });
                       var turnstileOps = blame.Snapshot()
                                               .Operations
                                               .Where(o => o.Operation.StartsWith(
                                                          "turnstile.",
                                                          StringComparison.Ordinal))
                                               .Select(o => new
                                               {
                                                   operation = o.Operation,
                                                   calls     = o.Calls,
                                                   avgMs     = o.AvgMs,
                                                   maxMs     = o.MaxMs,
                                                   errors    = o.Errors
                                               });

                       return Results.Ok(new
                       {
                           browsers,
                           cachedClearances = cached,
                           turnstileOps,
                           pinnedChromium = ChromiumDownloader.PinnedVersion,
                           platform       = ChromiumDownloader.GetPlatformSlug(),
                           cacheDir       = solver.BrowserCacheRoot
                       });
                   });

        app.MapPost("/api/v1/solver/clearance",
                    (SolverClearanceRequest req, TurnstileService solver) =>
                    {
                        if (string.IsNullOrWhiteSpace(req.Host) || string.IsNullOrWhiteSpace(req.Value))
                        {
                            return Results.BadRequest(new
                            {
                                error = "Host ve cf_clearance değeri gerekli."
                            });
                        }

                        var result = solver.StoreManual(req.Host, req.Value, req.ExpiresInHours);
                        if (!result.Success)
                        {
                            return Results.BadRequest(new
                            {
                                error = result.Error ?? "Kayıt başarısız."
                            });
                        }

                        return Results.Ok(new
                        {
                            host      = result.Host,
                            expiresAt = result.ExpiresAtUtc
                        });
                    });

        return app;
    }
}

using Migurdex.Shared.Interfaces;

namespace Migurdex.Api.Endpoints;

public static class StatsEndpoints
{
    public static IEndpointRouteBuilder MapStatsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/stats",
                   (IBlameCollector blame) =>
                   {
                       var report = blame.Snapshot();
                       blame.SaveIfStale();
                       return Results.Ok(report);
                   });
        return app;
    }
}

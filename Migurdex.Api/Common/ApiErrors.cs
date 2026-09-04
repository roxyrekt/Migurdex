namespace Migurdex.Api.Common;

public static class ApiErrors
{
    public static IResult BadRequest(string message)
    {
        return Results.Json(new
                            {
                                error = message
                            },
                            statusCode: StatusCodes.Status400BadRequest);
    }

    public static IResult NotFound(string message)
    {
        return Results.Json(new
                            {
                                error = message
                            },
                            statusCode: StatusCodes.Status404NotFound);
    }
}

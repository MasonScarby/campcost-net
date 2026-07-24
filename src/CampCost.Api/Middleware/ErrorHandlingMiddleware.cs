using System.Net;
using System.Text.Json;
using Sentry;

namespace CampCost.Api.Middleware;

public class ErrorHandlingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ErrorHandlingMiddleware> _logger;

    public ErrorHandlingMiddleware(RequestDelegate next, ILogger<ErrorHandlingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "Unauthorized request");
            await WriteError(context, HttpStatusCode.Unauthorized, ex.Message);
        }
        catch (Exception ex)
        {
            // This middleware swallows the exception to shape the JSON response, so Sentry's
            // automatic capture (which needs the exception to propagate out of the pipeline)
            // would never see it — capture it explicitly here. No-op if Sentry has no DSN.
            SentrySdk.CaptureException(ex);
            _logger.LogError(ex, "Unhandled exception");
            var detail = context.RequestServices
                .GetRequiredService<IHostEnvironment>().IsDevelopment()
                ? ex.ToString()
                : "An unexpected error occurred";
            await WriteError(context, HttpStatusCode.InternalServerError, detail);
        }
    }

    private static Task WriteError(HttpContext context, HttpStatusCode status, string message)
    {
        context.Response.StatusCode = (int)status;
        context.Response.ContentType = "application/json";
        return context.Response.WriteAsync(JsonSerializer.Serialize(new { error = message }));
    }
}

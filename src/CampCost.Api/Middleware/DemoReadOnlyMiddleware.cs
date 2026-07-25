using System.Security.Claims;

namespace CampCost.Api.Middleware;

// Makes the public demo account read-only. Every write in CampCost goes through the API as the
// `postgres` role (which bypasses RLS), so a single user can't be made read-only at the database
// layer — it has to be enforced here. When the authenticated user is the configured demo user,
// any mutating request (POST/PUT/PATCH/DELETE) is rejected with 403; GET/HEAD pass through so the
// demo can still be browsed. No-op when Demo:UserId is unset (e.g. local dev), so nothing changes
// there. Unauthenticated writes (the Plaid + Stripe webhooks) have no user id and never match.
public class DemoReadOnlyMiddleware
{
    private readonly RequestDelegate _next;
    private readonly string? _demoUserId;

    public DemoReadOnlyMiddleware(RequestDelegate next, IConfiguration config)
    {
        _next = next;
        _demoUserId = config["Demo:UserId"];
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!string.IsNullOrWhiteSpace(_demoUserId) && IsMutation(context.Request.Method))
        {
            var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier)
                         ?? context.User.FindFirstValue("sub");

            if (string.Equals(userId, _demoUserId, StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(
                    new { error = "This is a read-only demo account. Sign up for your own to make changes." });
                return;
            }
        }

        await _next(context);
    }

    private static bool IsMutation(string method) =>
        HttpMethods.IsPost(method) || HttpMethods.IsPut(method)
        || HttpMethods.IsPatch(method) || HttpMethods.IsDelete(method);
}

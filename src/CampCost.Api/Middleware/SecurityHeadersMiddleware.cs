namespace CampCost.Api.Middleware;

// Adds security response headers to every API response. The frontend (campcost.net) sets its own
// headers at the Caddy layer; this covers the API origin (api.campcost.net / the Railway URL).
// Deliberately minimal — no CSP (the API returns JSON, not documents, so CSP buys nothing) and no
// X-Frame-Options (a JSON API is never framed). Set before the rest of the pipeline so the headers
// are present on error, 401, and 429 responses too.
//
// HSTS is emitted unconditionally: Railway terminates TLS at the edge, so the app usually sees an
// http request internally, but the real client connection is https and browsers ignore HSTS
// received over plaintext anyway. NOT preloaded / no includeSubDomains yet — mirrors the staged
// rollout on the frontend.
public class SecurityHeadersMiddleware
{
    private readonly RequestDelegate _next;

    public SecurityHeadersMiddleware(RequestDelegate next) => _next = next;

    public Task InvokeAsync(HttpContext context)
    {
        // TryAdd, not indexer assignment: never clobber a header a reverse proxy or future
        // middleware may already have set. We're currently the only writer, so this is purely
        // defensive.
        var headers = context.Response.Headers;
        headers.TryAdd("Strict-Transport-Security", "max-age=31536000");
        headers.TryAdd("X-Content-Type-Options", "nosniff");
        headers.TryAdd("Referrer-Policy", "strict-origin-when-cross-origin");
        headers.TryAdd("Permissions-Policy", "camera=(), microphone=(), geolocation=(), payment=()");
        headers.TryAdd("Cross-Origin-Resource-Policy", "same-site");

        return _next(context);
    }
}

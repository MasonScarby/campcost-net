using System.Security.Claims;
using System.Threading.RateLimiting;
using CampCost.Api.Middleware;
using CampCost.Infrastructure;
using CampCost.Infrastructure.Data;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);

// --- Sentry error monitoring ---
// No-op when Sentry:Dsn is unset (local dev) — so nothing changes locally. In prod, set
// Sentry__Dsn in Railway. SendDefaultPii=false + default request-body capture off means bank
// data / transaction payloads / tokens are never attached to events. Errors only, no perf traces.
builder.WebHost.UseSentry(o =>
{
    o.Dsn = builder.Configuration["Sentry:Dsn"] ?? string.Empty;
    o.Environment = builder.Environment.EnvironmentName;
    o.SendDefaultPii = false;
    o.TracesSampleRate = 0;
    o.MinimumEventLevel = Microsoft.Extensions.Logging.LogLevel.Error;
});

// --- Infrastructure: DB + Plaid + all scoped services ---
builder.Services.AddInfrastructure(builder.Configuration);

// --- JWT auth: validates Supabase-issued RS256 tokens ---
// Issuer and audience are pinned: a token from any other Supabase project (same
// signing infra, different issuer) or with a non-user audience is rejected.
var supabaseIssuer = builder.Configuration["Supabase:Url"] + "/auth/v1";
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.Authority = supabaseIssuer;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer   = true,
            ValidIssuer      = supabaseIssuer,
            ValidateAudience = true,
            ValidAudience    = "authenticated",
            ValidateLifetime = true
        };
    });

// --- Rate limiting ---
// plaid-per-user: 10 req/min per authenticated user on /api/plaid/* (Plaid calls are
// expensive and hit our Plaid API quota). Unauthenticated requests bucket by IP —
// they get rejected by auth anyway, but they must not bypass the limiter.
// webhook-per-ip: 30 req/min per source IP on the unauthenticated webhook endpoint.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    options.AddPolicy("plaid-per-user", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.User.FindFirstValue(ClaimTypes.NameIdentifier)
                ?? context.User.FindFirstValue("sub")
                ?? context.Connection.RemoteIpAddress?.ToString()
                ?? "anonymous",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));

    options.AddPolicy("webhook-per-ip", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 30,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));
});

// --- CORS: origins driven by config so production can inject the Vercel URL ---
// Production env var: Cors__AllowedOrigins__0=https://your-app.vercel.app
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
    ?? ["http://localhost:5173", "http://localhost:5174"];

builder.Services.AddCors(options =>
    options.AddDefaultPolicy(policy =>
        policy.WithOrigins(allowedOrigins)
              .AllowAnyHeader()
              .AllowAnyMethod()));

builder.Services.AddControllers()
    // EF fixes up navigations both ways (Trip.BudgetCategories ↔ BudgetCategory.Trip),
    // so returning a tracked entity would loop forever during serialization and 500
    // AFTER the save (trip created, response dead). IgnoreCycles emits null at the
    // point of the cycle instead.
    .AddJsonOptions(o =>
        o.JsonSerializerOptions.ReferenceHandler =
            System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles);
builder.Services.AddEndpointsApiExplorer();

// --- Swagger with JWT Bearer support ---
// Click "Authorize" in Swagger UI, paste your Supabase access_token to test protected endpoints.
// Get it from the browser console: (await supabase.auth.getSession()).data.session.access_token
builder.Services.AddSwaggerGen(c =>
{
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name        = "Authorization",
        Type        = SecuritySchemeType.Http,
        Scheme      = "bearer",
        BearerFormat = "JWT",
        In          = ParameterLocation.Header,
        Description = "Paste your Supabase JWT here.\n\n" +
                      "Get it from the browser console:\n" +
                      "(await supabase.auth.getSession()).data.session.access_token"
    });
    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id   = "Bearer"
                }
            },
            Array.Empty<string>()
        }
    });
});

var app = builder.Build();

// Outermost so the headers land on every response, including errors, 401s, and 429s.
app.UseMiddleware<SecurityHeadersMiddleware>();
app.UseMiddleware<ErrorHandlingMiddleware>();
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
// After UseAuthentication so the per-user policy can partition on the JWT subject.
app.UseRateLimiter();

// After UseAuthentication so the user's claims are available. Rejects writes from the
// public demo account (Demo:UserId) so shared demo credentials can't be used to modify data.
app.UseMiddleware<DemoReadOnlyMiddleware>();

// Swagger is a development tool only — never expose the API surface (or the
// paste-your-JWT UI) in production.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapControllers();

// Liveness — is the process up? (cheap, no dependencies)
app.MapGet("/", () => Results.Ok(new { status = "ok" }));

// Readiness — is the process AND its database reachable? Point UptimeRobot here so a DB outage
// pages you instead of the app looking "up". Returns 503 when the DB can't be reached.
// GET + HEAD: uptime monitors (UptimeRobot et al.) probe with HEAD by default — a GET-only route
// answers HEAD with 405, which reads as "down". HEAD runs the check and returns status-only.
app.MapMethods("/health", new[] { "GET", "HEAD" }, async (CampCostDbContext db) =>
    await db.Database.CanConnectAsync()
        ? Results.Ok(new { status = "healthy" })
        : Results.StatusCode(StatusCodes.Status503ServiceUnavailable));

app.Run();

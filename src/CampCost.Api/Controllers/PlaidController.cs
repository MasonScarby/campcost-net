using CampCost.Core.Entities;
using CampCost.Core.Interfaces;
using CampCost.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CampCost.Api.Controllers;

[ApiController]
[Route("api/plaid")]
[Authorize]
[EnableRateLimiting("plaid-per-user")]
public class PlaidController : ControllerBase
{
    private readonly IPlaidService _plaid;
    private readonly ITransactionSyncService _sync;
    private readonly IPlaidWebhookVerifier _webhookVerifier;
    private readonly CampCostDbContext _db;
    private readonly ILogger<PlaidController> _logger;

    public PlaidController(
        IPlaidService plaid,
        ITransactionSyncService sync,
        IPlaidWebhookVerifier webhookVerifier,
        CampCostDbContext db,
        ILogger<PlaidController> logger)
    {
        _plaid = plaid;
        _sync = sync;
        _webhookVerifier = webhookVerifier;
        _db = db;
        _logger = logger;
    }

    private Guid UserId => Guid.Parse(
        User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? User.FindFirstValue("sub")
        ?? throw new UnauthorizedAccessException("No user ID in token"));

    // POST /api/plaid/link-token
    // Creates a Plaid Link token so the frontend can open the bank-connection UI
    [HttpPost("link-token")]
    public async Task<IActionResult> CreateLinkToken()
    {
        var token = await _plaid.CreateLinkTokenAsync(UserId.ToString());
        return Ok(new { link_token = token });
    }

    // POST /api/plaid/exchange-token
    // Swaps the short-lived public token for a permanent access token.
    // Upserts on item_id — re-linking the same bank updates the access token.
    [HttpPost("exchange-token")]
    public async Task<IActionResult> ExchangeToken([FromBody] ExchangeRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.PublicToken))
            return BadRequest(new { error = "public_token is missing from request body" });

        var (accessToken, itemId) = await _plaid.ExchangePublicTokenAsync(req.PublicToken);

        // UserId filter is security-critical: without it, one user re-linking an item_id
        // that belongs to another user would overwrite that user's connection.
        var existing = await _db.PlaidConnections
            .FirstOrDefaultAsync(c => c.ItemId == itemId && c.UserId == UserId);

        if (existing is not null)
        {
            existing.AccessToken = accessToken;
            if (req.InstitutionName is not null)
                existing.InstitutionName = req.InstitutionName;
        }
        else
        {
            _db.PlaidConnections.Add(new PlaidConnection
            {
                Id = Guid.NewGuid(),
                UserId = UserId,
                AccessToken = accessToken,
                ItemId = itemId,
                InstitutionName = req.InstitutionName ?? "Unknown",
                CreatedAt = DateTime.UtcNow
            });
        }
        await _db.SaveChangesAsync();

        return Ok(new { success = true, item_id = itemId });
    }

    // POST /api/plaid/sync
    // Without trip_id: assigns transactions to whichever active trip matches the date.
    // With trip_id: fetches exactly that trip's date range and routes everything to
    // that trip, correcting any wrong-trip assignments from previous syncs.
    [HttpPost("sync")]
    public async Task<IActionResult> Sync([FromQuery(Name = "trip_id")] Guid? tripId)
    {
        var result = tripId.HasValue
            ? await _sync.SyncForTripAsync(UserId, tripId.Value)
            : await _sync.SyncForUserAsync(UserId);
        return Ok(result);
    }

    // POST /api/plaid/webhook
    // Receives push notifications from Plaid. Only acts on TRANSACTIONS webhooks.
    // Looks up the affected user by item_id and syncs only them.
    //
    // The endpoint is unauthenticated, so every request MUST carry a valid
    // `Plaid-Verification` JWT (signed by Plaid, body hash bound) or it is rejected
    // with 401 before anything is parsed or synced. The raw body is read manually
    // because signature verification hashes the exact bytes Plaid sent.
    [HttpPost("webhook")]
    [AllowAnonymous]
    [EnableRateLimiting("webhook-per-ip")]
    public async Task<IActionResult> Webhook()
    {
        string rawBody;
        using (var reader = new StreamReader(Request.Body))
            rawBody = await reader.ReadToEndAsync();

        var verificationJwt = Request.Headers["Plaid-Verification"].FirstOrDefault();
        if (string.IsNullOrEmpty(verificationJwt) ||
            !await _webhookVerifier.VerifyAsync(rawBody, verificationJwt))
        {
            _logger.LogWarning("Rejected Plaid webhook: missing or invalid Plaid-Verification signature");
            return Unauthorized();
        }

        WebhookPayload? payload = null;
        try
        {
            payload = JsonSerializer.Deserialize<WebhookPayload>(rawBody);
        }
        catch (JsonException)
        {
            // Signed-but-unparseable should never happen; acknowledge so Plaid doesn't retry forever.
        }

        if (payload?.WebhookType != "TRANSACTIONS")
            return Ok(new { received = true });

        // Unknown or missing item_id → log, acknowledge, do nothing. This endpoint
        // must NEVER fall back to syncing all users.
        if (payload.ItemId is null)
        {
            _logger.LogWarning("Plaid TRANSACTIONS webhook had no item_id — ignored");
            return Ok(new { received = true });
        }

        var connection = await _db.PlaidConnections
            .FirstOrDefaultAsync(c => c.ItemId == payload.ItemId);

        if (connection is null)
        {
            _logger.LogWarning("Plaid webhook for unknown item_id {ItemId} — ignored", payload.ItemId);
            return Ok(new { received = true });
        }

        await _sync.SyncForUserAsync(connection.UserId);
        return Ok(new { received = true });
    }
}

public record ExchangeRequest(
    [property: JsonPropertyName("public_token")] string PublicToken,
    [property: JsonPropertyName("institution_name")] string? InstitutionName
);

public record WebhookPayload(
    [property: JsonPropertyName("webhook_type")] string? WebhookType,
    [property: JsonPropertyName("item_id")] string? ItemId
);

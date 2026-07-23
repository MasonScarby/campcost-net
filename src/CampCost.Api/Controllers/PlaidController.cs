using CampCost.Api.Contracts;
using CampCost.Core.Entities;
using CampCost.Core.Interfaces;
using CampCost.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using System.Text.Json;

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
    private readonly IAccountService _account;
    private readonly CampCostDbContext _db;
    private readonly ILogger<PlaidController> _logger;

    public PlaidController(
        IPlaidService plaid,
        ITransactionSyncService sync,
        IPlaidWebhookVerifier webhookVerifier,
        IAccountService account,
        CampCostDbContext db,
        ILogger<PlaidController> logger)
    {
        _plaid = plaid;
        _sync = sync;
        _webhookVerifier = webhookVerifier;
        _account = account;
        _db = db;
        _logger = logger;
    }

    private Guid UserId => Guid.Parse(
        User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? User.FindFirstValue("sub")
        ?? throw new UnauthorizedAccessException("No user ID in token"));

    // Import-history windows the user may pick (days). Whitelist — the ONLY values ever
    // forwarded to Plaid's days_requested. Anything else falls back to the default.
    private static readonly int[] AllowedImportDays = { 14, 30, 60, 90 };
    private const int DefaultImportDays = 30;

    // POST /api/plaid/link-token
    // Creates a Plaid Link token so the frontend can open the bank-connection UI.
    // Optional body { "days": 14|30|60|90 } bounds the initial transaction backfill;
    // the value is whitelisted server-side so a tampered request can't widen the fetch.
    [HttpPost("link-token")]
    public async Task<IActionResult> CreateLinkToken([FromBody] LinkTokenRequest? req = null)
    {
        var days = req?.Days is int d && AllowedImportDays.Contains(d) ? d : DefaultImportDays;
        var token = await _plaid.CreateLinkTokenAsync(UserId.ToString(), days);
        return Ok(new { link_token = token });
    }

    // POST /api/plaid/update-link-token  { "id": "<connection row id>" }
    // Returns a Link token in UPDATE mode for that connection, so the user can re-authenticate
    // the SAME Item (fixes ITEM_LOGIN_REQUIRED / PENDING_EXPIRATION) without minting a new Item —
    // which is what avoids the duplicate history a disconnect+reconnect causes. Scoped by
    // connection id + JWT user, so a user can only re-auth their own bank.
    [HttpPost("update-link-token")]
    public async Task<IActionResult> CreateUpdateLinkToken([FromBody] UpdateLinkTokenRequest req)
    {
        if (req.Id == Guid.Empty)
            return BadRequest(new { error = "id is required" });

        var connection = await _db.PlaidConnections
            .FirstOrDefaultAsync(c => c.Id == req.Id && c.UserId == UserId);
        if (connection is null)
            return NotFound(new { error = "No connected bank found with that id." });

        var token = await _plaid.CreateUpdateLinkTokenAsync(UserId.ToString(), connection.AccessToken);
        return Ok(new { link_token = token });
    }

    // POST /api/plaid/exchange-token
    // Swaps the short-lived public token for a permanent access token.
    // Upserts on item_id — re-linking the same bank updates the access token.
    [HttpPost("exchange-token")]
    public async Task<IActionResult> ExchangeToken([FromBody] ExchangeRequest req)
    {
        // Defense in depth: connecting a bank requires 2FA. Supabase stamps the JWT with
        // aal2 only after a verified MFA challenge — never trust the client gate alone.
        // Checked before any Plaid call so an un-stepped-up session never reaches Plaid.
        if (!string.Equals(User.FindFirstValue("aal"), "aal2", StringComparison.OrdinalIgnoreCase))
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                error = "Two-factor authentication is required before connecting a bank account.",
                code = "mfa_required"
            });

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

    // POST /api/plaid/reset-cursor
    // Forces the next sync to re-pull the full available history from Plaid by clearing the
    // stored cursor on every connection this user owns. Stored transactions are NOT deleted:
    // dedup on plaid_transaction_id (including soft-deleted rows) prevents re-inserting, so
    // this is a safe "re-pull from scratch" that never destroys the source of truth. Pair it
    // with POST /api/plaid/sync?trip_id= to re-pull and re-claim in one user action.
    [HttpPost("reset-cursor")]
    public async Task<IActionResult> ResetCursor()
    {
        var connections = await _db.PlaidConnections
            .Where(c => c.UserId == UserId)
            .ToListAsync();

        foreach (var c in connections)
            c.SyncCursor = null;

        await _db.SaveChangesAsync();

        _logger.LogInformation(
            "Reset sync cursor on {Count} connection(s) for user {UserId} — next sync re-pulls full history",
            connections.Count, UserId);

        return Ok(new { success = true, connections_reset = connections.Count });
    }

    // POST /api/plaid/disconnect  { "id": "<connection row id>" }
    // Disconnects a linked bank: calls Plaid /item/remove to invalidate the access token, then
    // deletes the local connection (historical transactions are kept, just detached). Required
    // so a user can revoke bank access — and so we stop holding a live token we no longer need.
    // Keyed off the connection row id (the only identifier the browser can read) + the JWT user,
    // so a user can only disconnect their own bank. This MUST go through the API: a direct
    // client-side delete cannot call Plaid /item/remove and is blocked by RLS anyway.
    [HttpPost("disconnect")]
    public async Task<IActionResult> Disconnect([FromBody] DisconnectRequest req)
    {
        if (req.Id == Guid.Empty)
            return BadRequest(new { error = "id is required" });

        var ok = await _account.DisconnectBankByIdAsync(UserId, req.Id);
        return ok
            ? Ok(new { success = true })
            : NotFound(new { error = "No connected bank found with that id." });
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

        // Only TRANSACTIONS (new/changed spend) and ITEM (connection health) webhooks are actionable.
        if (payload?.WebhookType is not ("TRANSACTIONS" or "ITEM"))
            return Ok(new { received = true });

        // Unknown or missing item_id → log, acknowledge, do nothing. This endpoint
        // must NEVER fall back to syncing all users.
        if (payload.ItemId is null)
        {
            _logger.LogWarning("Plaid {Type} webhook had no item_id — ignored", payload.WebhookType);
            return Ok(new { received = true });
        }

        var connection = await _db.PlaidConnections
            .FirstOrDefaultAsync(c => c.ItemId == payload.ItemId);

        if (connection is null)
        {
            _logger.LogWarning("Plaid webhook for unknown item_id {ItemId} — ignored", payload.ItemId);
            return Ok(new { received = true });
        }

        if (payload.WebhookType == "ITEM")
        {
            await HandleItemWebhookAsync(connection, payload);
            return Ok(new { received = true });
        }

        await _sync.SyncForUserAsync(connection.UserId);
        return Ok(new { received = true });
    }

    // Flags/unflags a connection for re-authentication based on ITEM webhook codes.
    // needs_reauth drives the UI's update-mode "Reconnect" action; a successful sync also
    // clears it (see TransactionSyncService), so this only has to set it and honor LOGIN_REPAIRED.
    private async Task HandleItemWebhookAsync(PlaidConnection connection, WebhookPayload payload)
    {
        var needsReauth =
            (payload.WebhookCode == "ERROR" && payload.Error?.ErrorCode == "ITEM_LOGIN_REQUIRED")
            || payload.WebhookCode == "PENDING_EXPIRATION";

        if (needsReauth && !connection.NeedsReauth)
        {
            connection.NeedsReauth = true;
            await _db.SaveChangesAsync();
            _logger.LogInformation(
                "Item {ItemId} flagged needs_reauth ({Code})", payload.ItemId, payload.WebhookCode);
        }
        else if (payload.WebhookCode == "LOGIN_REPAIRED" && connection.NeedsReauth)
        {
            connection.NeedsReauth = false;
            await _db.SaveChangesAsync();
            _logger.LogInformation("Item {ItemId} re-auth cleared (LOGIN_REPAIRED)", payload.ItemId);
        }
    }
}

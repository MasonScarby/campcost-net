using System.Net.Http.Headers;
using CampCost.Core.Interfaces;
using CampCost.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CampCost.Infrastructure.Services;

/// <summary>
/// Account-lifecycle operations (disconnect a bank, delete an account). Plaid item removal is
/// best-effort — a Plaid error never blocks local data removal, because the user's right to
/// delete their data must not depend on a third party being reachable.
/// </summary>
public class AccountService : IAccountService
{
    private readonly CampCostDbContext _db;
    private readonly IPlaidService _plaid;
    private readonly IConfiguration _config;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<AccountService> _logger;

    public AccountService(
        CampCostDbContext db,
        IPlaidService plaid,
        IConfiguration config,
        IHttpClientFactory httpFactory,
        ILogger<AccountService> logger)
    {
        _db = db;
        _plaid = plaid;
        _config = config;
        _httpFactory = httpFactory;
        _logger = logger;
    }

    public async Task<bool> DisconnectBankAsync(Guid userId, string itemId)
    {
        var connection = await _db.PlaidConnections
            .FirstOrDefaultAsync(c => c.UserId == userId && c.ItemId == itemId);
        if (connection is null)
            return false;

        await SafeRemoveItemAsync(connection.AccessToken);

        // Deleting the connection DETACHES its transactions but keeps them: since migration 008,
        // transactions.plaid_connection_id is `on delete set null`, so the DB nulls that FK on the
        // affected rows instead of cascade-deleting them. The user's spend history survives an
        // unlink (only a full account deletion purges it). EF isn't tracking those rows, so the
        // DB-side SET NULL happens without an in-memory conflict.
        _db.PlaidConnections.Remove(connection);
        await _db.SaveChangesAsync();

        _logger.LogInformation("Disconnected bank item {ItemId} for user {UserId}", itemId, userId);
        return true;
    }

    public async Task<bool> DisconnectBankByIdAsync(Guid userId, Guid connectionId)
    {
        // Look up by row id AND user id: the id comes from the browser, so scoping to the
        // authenticated user is what stops one user disconnecting another's connection.
        var connection = await _db.PlaidConnections
            .FirstOrDefaultAsync(c => c.UserId == userId && c.Id == connectionId);
        if (connection is null)
            return false;

        // Revoke the live token at Plaid before dropping the row (best-effort — see SafeRemove).
        await SafeRemoveItemAsync(connection.AccessToken);

        // Deleting the connection DETACHES its transactions but keeps them (migration 008 makes
        // transactions.plaid_connection_id `on delete set null`). Spend history survives an unlink.
        _db.PlaidConnections.Remove(connection);
        await _db.SaveChangesAsync();

        _logger.LogInformation(
            "Disconnected bank connection {ConnectionId} for user {UserId}", connectionId, userId);
        return true;
    }

    public async Task<int> ClearTransactionsAsync(Guid userId)
    {
        // Hard-delete every transaction for this user, INCLUDING soft-deleted rows
        // (IgnoreQueryFilters) — otherwise dedup on plaid_transaction_id would block a clean
        // re-import. Bank connections and manual `expenses` are left untouched.
        var txns = await _db.Transactions
            .IgnoreQueryFilters()
            .Where(t => t.UserId == userId)
            .ToListAsync();
        _db.Transactions.RemoveRange(txns);

        // Reset cursors so the next sync re-pulls the full available history as a fresh set.
        var connections = await _db.PlaidConnections
            .Where(c => c.UserId == userId)
            .ToListAsync();
        foreach (var c in connections)
            c.SyncCursor = null;

        await _db.SaveChangesAsync();

        _logger.LogInformation(
            "Cleared {Count} transaction(s) and reset {Conns} cursor(s) for user {UserId}",
            txns.Count, connections.Count, userId);
        return txns.Count;
    }

    public async Task DeleteAccountAsync(Guid userId)
    {
        // 1) Invalidate every Plaid Item first (best-effort).
        var connections = await _db.PlaidConnections
            .Where(c => c.UserId == userId)
            .ToListAsync();
        foreach (var c in connections)
            await SafeRemoveItemAsync(c.AccessToken);

        // 2) Delete all app data, children before parents (FK-safe). IgnoreQueryFilters so
        //    soft-deleted transactions/expenses are purged too.
        var tripIds = await _db.Trips
            .Where(t => t.UserId == userId)
            .Select(t => t.Id)
            .ToListAsync();

        _db.Transactions.RemoveRange(
            await _db.Transactions.IgnoreQueryFilters().Where(t => t.UserId == userId).ToListAsync());
        _db.Expenses.RemoveRange(
            await _db.Expenses.IgnoreQueryFilters().Where(e => e.UserId == userId).ToListAsync());
        _db.BudgetCategories.RemoveRange(
            await _db.BudgetCategories.Where(b => tripIds.Contains(b.TripId)).ToListAsync());
        _db.TripStops.RemoveRange(
            await _db.TripStops.Where(s => tripIds.Contains(s.TripId)).ToListAsync());
        _db.Trips.RemoveRange(
            await _db.Trips.Where(t => t.UserId == userId).ToListAsync());
        _db.PlaidConnections.RemoveRange(connections);
        _db.Subscriptions.RemoveRange(
            await _db.Subscriptions.Where(s => s.UserId == userId).ToListAsync());

        await _db.SaveChangesAsync();

        // 3) Delete the Supabase auth user (best-effort — data is already gone regardless).
        await DeleteAuthUserAsync(userId);

        _logger.LogInformation("Deleted account and all data for user {UserId}", userId);
    }

    private async Task SafeRemoveItemAsync(string accessToken)
    {
        try
        {
            await _plaid.RemoveItemAsync(accessToken);
        }
        catch (Exception ex)
        {
            // Never let a Plaid outage block the user's data deletion.
            _logger.LogWarning(ex, "Plaid /item/remove failed during disconnect/delete — continuing");
        }
    }

    private async Task DeleteAuthUserAsync(Guid userId)
    {
        var url = _config["Supabase:Url"];
        var serviceKey = _config["Supabase:ServiceRoleKey"];
        if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(serviceKey))
        {
            _logger.LogWarning(
                "Supabase Url/ServiceRoleKey not configured — auth user {UserId} was NOT deleted (app data was). Configure the service role key to complete account deletion.",
                userId);
            return;
        }

        try
        {
            var http = _httpFactory.CreateClient();
            using var req = new HttpRequestMessage(
                HttpMethod.Delete, $"{url.TrimEnd('/')}/auth/v1/admin/users/{userId}");
            req.Headers.Add("apikey", serviceKey);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", serviceKey);

            var resp = await http.SendAsync(req);
            if (!resp.IsSuccessStatusCode)
                _logger.LogWarning(
                    "Supabase admin delete-user returned {Status} for {UserId}", resp.StatusCode, userId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Supabase admin delete-user call failed for {UserId}", userId);
        }
    }
}

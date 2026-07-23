using CampCost.Core.Interfaces;
using Going.Plaid;
using Going.Plaid.Entity;
using Going.Plaid.Item;
using Going.Plaid.Link;
using Going.Plaid.Transactions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CampCost.Infrastructure.Services;

public class PlaidService : IPlaidService
{
    /// <summary>Max page size allowed by /transactions/sync.</summary>
    private const int SyncPageSize = 500;

    private readonly PlaidClient _client;
    private readonly ILogger<PlaidService> _logger;
    private readonly string _webhookUrl;

    private readonly string _redirectUri;

    public PlaidService(PlaidClient client, IConfiguration config, ILogger<PlaidService> logger)
    {
        _client = client;
        _logger = logger;
        _webhookUrl = config["Plaid:WebhookUrl"] ?? "";
        _redirectUri = config["Plaid:RedirectUri"] ?? "";
    }

    public async Task<string> CreateLinkTokenAsync(string userId, int transactionDays)
    {
        var response = await _client.LinkTokenCreateAsync(new LinkTokenCreateRequest
        {
            User = new LinkTokenCreateRequestUser { ClientUserId = userId },
            ClientName = "CampCost",
            Products = new List<Products> { Products.Transactions },
            CountryCodes = new List<CountryCode> { CountryCode.Us },
            Language = Language.English,
            Webhook = string.IsNullOrEmpty(_webhookUrl) ? null : _webhookUrl,
            // Limits the initial backfill so we only fetch (and store) the history the user
            // asked for — less bank PII at rest, less noise. Whitelisted upstream in the controller.
            Transactions = new LinkTokenTransactions { DaysRequested = transactionDays }
        });
        return response.LinkToken;
    }

    public async Task<string> CreateUpdateLinkTokenAsync(string userId, string accessToken)
    {
        // Update mode: pass the existing access_token and OMIT Products/Transactions. Link
        // re-authenticates the SAME Item (same item_id, stable transaction_ids), so re-auth
        // never creates a new Item — which is exactly what avoids the duplicate-transaction
        // problem that a disconnect + reconnect produces.
        var response = await _client.LinkTokenCreateAsync(new LinkTokenCreateRequest
        {
            User = new LinkTokenCreateRequestUser { ClientUserId = userId },
            ClientName = "CampCost",
            CountryCodes = new List<CountryCode> { CountryCode.Us },
            Language = Language.English,
            Webhook = string.IsNullOrEmpty(_webhookUrl) ? null : _webhookUrl,
            AccessToken = accessToken
        });
        return response.LinkToken;
    }

    public async Task<(string AccessToken, string ItemId)> ExchangePublicTokenAsync(string publicToken)
    {
        var response = await _client.ItemPublicTokenExchangeAsync(
            new ItemPublicTokenExchangeRequest { PublicToken = publicToken });
        return (response.AccessToken, response.ItemId);
    }

    /// <summary>
    /// /transactions/sync — pages through the full delta set (has_more loop) and
    /// returns aggregated added/modified/removed plus the final cursor.
    /// </summary>
    public async Task<PlaidSyncDelta> SyncTransactionsAsync(string accessToken, string? cursor)
    {
        var added = new List<PlaidTransaction>();
        var modified = new List<PlaidTransaction>();
        var removed = new List<string>();
        var nextCursor = cursor;
        bool hasMore;

        do
        {
            var response = await _client.TransactionsSyncAsync(new TransactionsSyncRequest
            {
                AccessToken = accessToken,
                Cursor = nextCursor,
                Count = SyncPageSize,
                Options = new TransactionsSyncRequestOptions
                {
                    IncludeOriginalDescription = true
                }
            });

            added.AddRange(MapTransactions(response.Added));
            modified.AddRange(MapTransactions(response.Modified));
            removed.AddRange(
                (response.Removed ?? new List<RemovedTransaction>())
                    .Select(r => r.TransactionId)
                    .Where(id => !string.IsNullOrEmpty(id))
                    .Select(id => id!));

            nextCursor = response.NextCursor;
            hasMore = response.HasMore;
        } while (hasMore);

        return new PlaidSyncDelta(added, modified, removed, nextCursor ?? cursor ?? string.Empty);
    }

    public async Task RemoveItemAsync(string accessToken)
    {
        var response = await _client.ItemRemoveAsync(new ItemRemoveRequest
        {
            AccessToken = accessToken
        });

        // Going.Plaid surfaces Plaid API errors on the response rather than throwing. An
        // already-removed item is fine (the goal — token invalid — is achieved); log others
        // but don't throw, so account/data deletion is never blocked by a Plaid hiccup.
        if (response.Error is not null)
            _logger.LogWarning(
                "Plaid /item/remove returned {ErrorCode}: {ErrorMessage}",
                response.Error.ErrorCode, response.Error.ErrorMessage);
    }

    private IEnumerable<PlaidTransaction> MapTransactions(IReadOnlyList<Transaction>? transactions)
    {
        foreach (var t in transactions ?? Array.Empty<Transaction>())
        {
            // A transaction without a stable ID can never be deduplicated — skip it.
            // (Previously this fell back to a random GUID, which broke dedup permanently.)
            if (string.IsNullOrEmpty(t.TransactionId))
            {
                _logger.LogWarning(
                    "Skipping Plaid transaction with missing transaction_id (date: {Date}) — no stable ID means no dedup",
                    t.Date);
                continue;
            }

            if (t.Date is null)
            {
                _logger.LogWarning(
                    "Skipping Plaid transaction {TransactionId} with missing date — cannot match to a trip window",
                    t.TransactionId);
                continue;
            }

            yield return new PlaidTransaction(
                t.TransactionId,
                t.OriginalDescription ?? t.MerchantName ?? "",
                t.MerchantName,
                t.Amount ?? 0m,
                t.Date.Value,
                t.Pending ?? false,
                t.PendingTransactionId
            );
        }
    }
}

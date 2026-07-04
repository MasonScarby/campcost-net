using CampCost.Core.Interfaces;
using Going.Plaid;
using Going.Plaid.Entity;
using Going.Plaid.Item;
using Going.Plaid.Link;
using Going.Plaid.Transactions;
using Microsoft.Extensions.Configuration;

namespace CampCost.Infrastructure.Services;

public class PlaidService : IPlaidService
{
    private readonly PlaidClient _client;
    private readonly string _webhookUrl;

    private readonly string _redirectUri;

    public PlaidService(PlaidClient client, IConfiguration config)
    {
        _client = client;
        _webhookUrl = config["Plaid:WebhookUrl"] ?? "";
        _redirectUri = config["Plaid:RedirectUri"] ?? "";
    }

    public async Task<string> CreateLinkTokenAsync(string userId)
    {
        var response = await _client.LinkTokenCreateAsync(new LinkTokenCreateRequest
        {
            User = new LinkTokenCreateRequestUser { ClientUserId = userId },
            ClientName = "CampCost",
            Products = new List<Products> { Products.Transactions },
            CountryCodes = new List<CountryCode> { CountryCode.Us },
            Language = Language.English,
            Webhook = string.IsNullOrEmpty(_webhookUrl) ? null : _webhookUrl
        });
        return response.LinkToken;
    }

    public async Task<(string AccessToken, string ItemId)> ExchangePublicTokenAsync(string publicToken)
    {
        var response = await _client.ItemPublicTokenExchangeAsync(
            new ItemPublicTokenExchangeRequest { PublicToken = publicToken });
        return (response.AccessToken, response.ItemId);
    }

    public async Task<IReadOnlyList<PlaidTransaction>> GetTransactionsAsync(
        string accessToken, DateOnly startDate, DateOnly endDate)
    {
        var response = await _client.TransactionsGetAsync(new TransactionsGetRequest
        {
            AccessToken = accessToken,
            StartDate = startDate,
            EndDate = endDate
        });

        return (response.Transactions ?? new List<Going.Plaid.Entity.Transaction>()).Select(t => new PlaidTransaction(
            t.TransactionId ?? Guid.NewGuid().ToString(),
            t.OriginalDescription ?? t.MerchantName ?? "",
            t.MerchantName,
            t.Amount ?? 0m,
            t.Date ?? startDate,
            t.Pending ?? false
        )).ToList();
    }
}

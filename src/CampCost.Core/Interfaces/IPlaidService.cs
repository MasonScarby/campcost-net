namespace CampCost.Core.Interfaces;

public interface IPlaidService
{
    Task<string> CreateLinkTokenAsync(string userId);
    Task<(string AccessToken, string ItemId)> ExchangePublicTokenAsync(string publicToken);
    Task<IReadOnlyList<PlaidTransaction>> GetTransactionsAsync(
        string accessToken, DateOnly startDate, DateOnly endDate);
}

/// <summary>
/// Lightweight DTO — Plaid SDK types never leak into Core.
/// Name = raw transaction name, MerchantName = cleaned merchant name (may be null).
/// Amount is positive for debits (money out), negative for credits — same as Plaid convention.
/// </summary>
public record PlaidTransaction(
    string TransactionId,
    string Name,
    string? MerchantName,
    decimal Amount,
    DateOnly Date,
    bool Pending
);

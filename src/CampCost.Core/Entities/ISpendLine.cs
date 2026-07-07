namespace CampCost.Core.Entities;

/// <summary>
/// A single money-out line that counts toward a trip's spend. Implemented by both
/// <see cref="Transaction"/> (Plaid-sourced) and <see cref="Expense"/> (manual), so the
/// budget calculation can sum a trip's spend across both sources uniformly.
/// </summary>
public interface ISpendLine
{
    decimal Amount { get; }
    string Category { get; }
}

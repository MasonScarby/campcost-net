-- Restore split/adjust for BANK transactions (lost in the 004 transactions-table
-- refactor, where adjust became manual-expense-only).
--
-- The Plaid mirror stays immutable: `amount` is never edited. The adjustment is OUR
-- overlay ("my share was $X because …"), same pattern as expenses.adjusted_amount.
-- Effective spend = coalesce(adjusted_amount, amount). Writes go through the API only;
-- the browser gets read access to display splits.
alter table transactions
  add column if not exists adjusted_amount numeric(10,2),
  add column if not exists adjustment_note text;

grant select (adjusted_amount, adjustment_note) on transactions to authenticated;

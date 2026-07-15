-- ─────────────────────────────────────────────
-- 008: preserve transaction history when a bank is disconnected
-- ─────────────────────────────────────────────
-- Before: transactions.plaid_connection_id was `on delete cascade not null`, so disconnecting a
-- bank (deleting its plaid_connections row) also deleted every transaction synced from it —
-- silently wiping the user's spend history. That contradicts our "never hard-delete financial
-- history" rule (CLAUDE.md → Financial Rules).
--
-- After: the column is nullable and the FK is `on delete set null`. Disconnecting a bank now
-- DETACHES its transactions (plaid_connection_id → NULL) and keeps them. Full account deletion
-- still purges everything (AccountService.DeleteAccountAsync removes the rows explicitly by user_id).
--
-- Idempotent: safe to re-run. Apply in the Supabase SQL editor.

alter table transactions
  alter column plaid_connection_id drop not null;

alter table transactions
  drop constraint if exists transactions_plaid_connection_id_fkey;

alter table transactions
  add constraint transactions_plaid_connection_id_fkey
  foreign key (plaid_connection_id)
  references plaid_connections(id)
  on delete set null;

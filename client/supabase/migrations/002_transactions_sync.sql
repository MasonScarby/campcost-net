-- /transactions/sync migration support
-- Run this in the Supabase SQL editor (same as 001_init.sql).

-- Cursor for Plaid /transactions/sync — position of the last successfully applied
-- delta set, per connection. NULL = never synced (first sync pulls full history).
alter table plaid_connections
  add column if not exists sync_cursor text;

-- Soft-delete marker for expenses. Set when Plaid reports a transaction as `removed`
-- (e.g. a pending transaction that never settled). Financial history is never
-- hard-deleted; the API excludes soft-deleted rows via an EF global query filter.
alter table expenses
  add column if not exists deleted_at timestamptz;

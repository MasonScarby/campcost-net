-- Lock down browser access to Plaid credentials.
-- Run this in the Supabase SQL editor (same as 001_init.sql / 002_transactions_sync.sql).
--
-- Problem: RLS only restricts WHICH rows a user can see — an authenticated user could
-- still SELECT their own row's access_token / item_id / sync_cursor from the browser
-- (and an XSS or leaked session token could exfiltrate them). The Plaid access token
-- must only ever be readable by the backend, which connects as `postgres` and is not
-- affected by these grants.
--
-- Fix: revoke the default table-wide grants from browser roles and re-grant only the
-- columns the client actually uses:
--   ConnectBank.jsx  → select id, institution_name, last_sync_at (filtered by user_id);
--                      delete (disconnect)
--   Account.jsx      → select institution_name, last_sync_at (filtered by user_id)
--   TripDetail.jsx   → update last_sync_at (filtered by user_id)
-- access_token, item_id and sync_cursor are NOT granted. RLS policies from 001 still
-- apply on top of these grants, so users remain limited to their own rows.

revoke all on table plaid_connections from anon;
revoke all on table plaid_connections from authenticated;

grant select (id, user_id, institution_name, last_sync_at, created_at)
  on plaid_connections to authenticated;

grant update (last_sync_at)
  on plaid_connections to authenticated;

grant delete
  on plaid_connections to authenticated;

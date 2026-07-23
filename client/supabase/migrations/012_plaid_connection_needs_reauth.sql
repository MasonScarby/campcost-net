-- Migration 012: connection-health flag for Plaid update-mode re-auth.
-- Run this in the Supabase SQL editor (same as the earlier migrations).
--
-- When Plaid signals an Item needs re-authentication (an ITEM webhook: ERROR with
-- ITEM_LOGIN_REQUIRED, or PENDING_EXPIRATION), the backend sets needs_reauth = true. The UI
-- reads it to show a "Reconnect" action that opens Link in UPDATE mode — re-authing the SAME
-- Item, so item_id and transaction_ids stay stable and no duplicate history is created (unlike
-- a disconnect + reconnect, which mints a new Item). A successful sync clears the flag, as does
-- Plaid's own LOGIN_REPAIRED webhook.

alter table plaid_connections
  add column if not exists needs_reauth boolean not null default false;

-- The browser reads its own connections' health. Column-level SELECT only, consistent with
-- migration 003 (which withholds access_token / item_id / sync_cursor from the client).
grant select (needs_reauth) on plaid_connections to authenticated;

-- Hardening tied to the disconnect fix: disconnect now goes through the API
-- (POST /api/plaid/disconnect → Plaid /item/remove → server-side delete). The browser no longer
-- deletes plaid_connections rows directly, so revoke that grant. This permanently closes the
-- orphan-token hole: a client (or a compromised session) can no longer delete a connection row
-- and leave its Plaid access token live — removal must go through the backend, which revokes it.
revoke delete on table plaid_connections from authenticated;

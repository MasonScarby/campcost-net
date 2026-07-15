-- ─────────────────────────────────────────────
-- 009: lock down the subscriptions table (close the self-serve Pro upgrade hole)
-- Run in the Supabase SQL editor (same as 001–008).
-- ─────────────────────────────────────────────
-- Problem: 001_init.sql created
--     create policy "Users own their subscription"
--       on subscriptions for all using (auth.uid() = user_id);
-- `for all` covers INSERT/UPDATE/DELETE too, so an authenticated browser could run
--     supabase.from('subscriptions').update({ plan: 'pro', status: 'active' })
-- on its OWN row and PlanService would then read that user as Pro. The free-tier gates
-- (7-day trip, 50-txn cap) become unenforceable. Migration 003 locked plaid_connections
-- this same way; the subscriptions table was missed.
--
-- Fix (mirrors 003): the browser may only READ its plan/status for display. Every WRITE
-- goes through a trusted server context:
--   • the backend connects as `postgres` (not affected by these grants)
--   • the Stripe webhook writes via the service role (bypasses RLS)
--   • handle_new_user() (the signup trigger) is SECURITY DEFINER, so it still inserts the
--     initial free row regardless of these grants/policies.
-- Client reads plan via GET /api/account/plan (server-side, JWT-keyed) — the direct
-- browser SELECT here is only a display cache fallback (plan.js).

-- 1) Replace the over-broad "for all" policy with SELECT-only, still scoped to own row.
drop policy if exists "Users own their subscription" on subscriptions;
create policy "Users can read their own subscription"
  on subscriptions for select using (auth.uid() = user_id);

-- 2) Revoke default table-wide grants from the browser roles, then re-grant SELECT only
--    on the display columns. No INSERT/UPDATE/DELETE for anon/authenticated.
--    (Belt-and-suspenders with the policy: a write needs BOTH a grant and a permissive
--    policy — removing the write grant closes the hole even if a policy is re-added later.)
revoke all on table subscriptions from anon;
revoke all on table subscriptions from authenticated;

grant select (id, user_id, plan, status, updated_at)
  on subscriptions to authenticated;

-- stripe_customer_id is intentionally NOT granted to the browser.

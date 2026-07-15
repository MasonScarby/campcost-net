# Review guide — `fix/subscriptions-rls-lockdown`

## Why
`001_init.sql:114` created the `subscriptions` RLS policy as `for all using (auth.uid() = user_id)`.
`for all` includes UPDATE, so any logged-in user can run this from the browser console:

```js
await supabase.from('subscriptions').update({ plan: 'pro', status: 'active' }).eq('user_id', myId)
```

`PlanService.GetPlanAsync` then reads them as **Pro** → the 7-day trip limit and 50-txn/trip cap
stop applying. Free tier is unenforceable. Migration 003 locked `plaid_connections` the same way;
`subscriptions` was missed.

## Files to read (in order)
1. `client/supabase/migrations/009_lock_down_subscriptions.sql` — the whole fix. Read top to bottom:
   - drops the `for all` policy, replaces with **SELECT-only** (still `auth.uid() = user_id`)
   - revokes table-wide grants from `anon`/`authenticated`, re-grants **SELECT only** on display columns
   - `stripe_customer_id` deliberately NOT granted to the browser
2. `client/supabase/migrations/003_lock_down_plaid_connections.sql` — the pattern this mirrors (for confidence it's the house style).

## Why it's safe (verified)
- **Backend writes still work:** `PlanService.SetPlanAsync` writes via EF as the `postgres` connection — table grants don't apply to it. (Same reasoning as the 003 comment.)
- **Signup still works:** `handle_new_user()` (001) is `SECURITY DEFINER`, so it inserts the initial free row regardless of these grants/policies.
- **Client never writes subscriptions directly** — grep found zero `.from('subscriptions').update/insert/delete` in `client/src`. The only reference is a comment in `Account.jsx:90` noting the dev toggle goes through `POST /api/account/plan` (server-side).
- **Client reads still work:** browser only needs SELECT for the display cache; the authoritative read is `GET /api/account/plan` (server, JWT-keyed).
- 144/144 tests green (migration is SQL-only; no C# impact).

## No automated test — why
RLS policies + column grants are enforced by Postgres, not by EF. The test suite runs in-memory
(no RLS), so it cannot exercise this. Verification is the manual SQL check below. (The existing
`PlanService`/gate tests already cover that the *server* trusts only the DB row.)

## How to verify after applying (Supabase SQL editor)
Apply `009` in the **Supabase SQL editor**, then:

```sql
-- policy is now SELECT-only (expect one row, cmd = SELECT)
select cmd, qual from pg_policies where tablename = 'subscriptions';

-- authenticated has SELECT but NOT update/insert/delete
select privilege_type from information_schema.role_column_grants
  where grantee = 'authenticated' and table_name = 'subscriptions';
```

Then, logged in as a normal user in the app, in the browser console:
```js
await supabase.from('subscriptions').update({ plan:'pro' }).eq('user_id', (await supabase.auth.getUser()).data.user.id)
// expect: error, "permission denied for table subscriptions"  (or new row-level rejection)
```
Signup a fresh user → confirm a `free`/`active` row still auto-creates (trigger unaffected).

## Deploy note
Apply on **every** Supabase environment before launch (dev + prod). Pure additive/idempotent-ish:
re-running drops+recreates the policy and re-grants — safe to re-run.

# CampCost.NET — Fixes & Roadmap

Living tracker. Move items to Shipped as they land. CampCost-only.

## ✅ Shipped
- pending→settled dedup via `/transactions/sync`
- no-active-trips black hole gone (transactions store regardless of trip)
- overlapping-trip deterministic order (`OrderBy StartDate`)
- `trips[0]` null fix
- Plaid 500-txn cap fixed (`has_more` pagination)
- "not part of expense" → unassign transaction
- README + `docs/architecture.md`
- CI moved to .NET 8
- security: webhook signature verify, sync-all fallback removed, browser token lockdown,
  Swagger disabled in prod, JWT issuer/audience validation, rate limiting,
  ExchangeToken UserId filter, null-TransactionId GUID dedup bug

## 🐛 Open bugs
1. **TripDetail spend split** — header shows ~$33k, body shows $0 (two data paths in one screen)
2. **trip_stops entity ↔ schema mismatch** — code uses `arrival_date`/`location`; DB has `nights`/`location_name`. Planned Stops feature errors.
3. Timezone drift on date comparisons (LOW — 2-day buffer mostly covers)

## 🔒 Production-ready (before real users)
4. Vault-encrypt Plaid `access_token` (plaintext at rest today)
5. Plaid `/item/remove` on disconnect (prod-approval expects it; today only deletes the DB row)
6. Data deletion flow (account delete → purge tokens + transactions)
7. Structured logging + error tracking (Sentry free) + uptime monitor

## 💰 Monetization / fraud
8. Fraud gate v2 — 7-day free-trip max + 50-txn/trip cap + TOTP before bank connect (agent branch, unmerged)
9. Stripe — checkout, webhook → `subscriptions`, upgrade mid-flow
10. (research) Plaid multi-email-per-bank policy — is account-tying real?
11. (decision, needs data) pay-per-trip vs subscription — survey campers on trips/year

## 🚀 Product value
12. **Fuel estimator (anchor)** — state avg gas × miles/state, prepopulate the field, don't overbuild
13. Budget stand-ins — food = daily-budget × days; campground/tolls if data exists
14. AI-assisted estimation for data gaps (later)

## ❓ Verify
15. Does sync still pull after a trip's date range completes? (likely fixed by transactions-table — confirm)
16. Do stored transactions exactly match ALL of Plaid's? (reconciliation check)

---
**Next 3:** #1 (bug, today) → #8 (fraud, agent branch → you review) → #12 fuel estimator (you build).

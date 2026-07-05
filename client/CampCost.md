# CampCost — Build Roadmap

> "Connect your bank. We track every dollar your trip costs you — automatically."

**Stack:** React + Vite + Tailwind · Supabase (auth + DB) · Plaid (bank sync) · Stripe (payments) · Vercel

---

## Status

| Phase | Feature | Status |
|---|---|---|
| 1 | Project scaffold + routing | ✅ Done |
| 1 | Supabase schema (6 tables + RLS) | ✅ Done |
| 1 | Auth — Google OAuth + email/password | ✅ Done |
| 1 | Dashboard — trip list + budget bars | ✅ Done |
| 1 | Trip creation wizard (2-step) | ✅ Done |
| 1 | Trip detail — budget breakdown + cash quick-add | ✅ Done |
| 1 | Transaction review queue UI | ✅ Done |
| 2 | Plaid bank connection | 🔜 Next |
| 2 | Transaction auto-import + sync | 🔜 Next |
| 2 | Auto-categorization by merchant | 🔜 Next |
| 2 | Plaid webhooks (same-day pending tx) | 🔜 Next |
| 3 | Campground stop planner | ⬜ Pending |
| 3 | Fuel cost estimator | ⬜ Pending |
| 3 | Cost-per-night + cost-per-mile stats | ⬜ Pending |
| 3 | Category breakdown chart (Recharts) | ⬜ Pending |
| 4 | Stripe Checkout + Pro plan | ⬜ Pending |
| 4 | Free tier gate (1 trip limit) | ⬜ Pending |
| 4 | PDF export (react-pdf) | ⬜ Pending |
| 5 | Landing page + pricing | ⬜ Pending |
| 5 | Mobile responsive pass | ⬜ Pending |
| 5 | Deploy to Vercel | ⬜ Pending |
| 5 | Launch post on LinkedIn | ⬜ Pending |

---

## Phase 2 — Plaid Bank Sync (Next)

**Goal:** User connects bank once → transactions auto-import during trip date range → review queue shows pending txs same day.

### What to build
1. **Plaid developer account** — sandbox credentials from dashboard.plaid.com
2. **Express backend** (port 3002) with two endpoints:
   - `POST /api/plaid/link-token` — creates a Plaid Link token for the frontend
   - `POST /api/plaid/exchange-token` — exchanges public token → access token, stores in Supabase
   - `POST /api/plaid/sync` — pulls transactions from Plaid, inserts into `expenses` table
   - `POST /api/plaid/webhook` — receives Plaid webhook when new transactions are available
3. **PlaidLink component** — wraps `react-plaid-link`, calls backend on success
4. **Auto-categorization** — merchant name → expense category mapping
5. **ConnectBank page** — wire up real Plaid flow (currently stubbed)

### Plaid sandbox
- Free, unlimited testing with fake bank accounts
- Test credentials: `user_good` / `pass_good`
- Transactions appear instantly in sandbox mode

### Auto-categorization rules
| Merchant keywords | Category |
|---|---|
| Shell, BP, Chevron, Pilot, Love's, Flying J, ExxonMobil | fuel |
| KOA, Hipcamp, ReserveAmerica, campground, RV park | campground |
| Walmart, Kroger, Safeway, Aldi, grocery, supermarket | food_groceries |
| REI, Cabela's, Bass Pro, gear | gear |
| AutoZone, O'Reilly, mechanic, repair | repairs |
| Propane, AmeriGas, utilities | propane_utilities |

### Pending transactions
- Plaid returns pending txs 1-4 hours after purchase
- Webhook `TRANSACTIONS_SYNC_UPDATES_AVAILABLE` triggers sync
- UX: make purchases all day → open app at night → everything is in the review queue

---

## Phase 3 — Value Features

### Cost stats (auto-calculated)
- **Cost per night** = total spent ÷ trip nights
- **Cost per mile** = total spent ÷ total miles
- **Daily burn rate** = total spent ÷ days elapsed

### Campground planner
- Add stops: location, type (boondock/dispersed/partial/full hookup), cost/night × nights
- Feeds into budget forecast before the trip

### Fuel estimator
- Input: estimated miles + vehicle MPG + $/gallon
- Output: estimated fuel cost → pre-fills fuel budget category

### Charts
- Category donut chart (Recharts)
- Daily spend line chart
- Actuals vs budget bar chart

---

## Phase 4 — Monetization

### Free tier
- 1 active trip
- 1 connected bank account
- 90-day transaction history

### Pro — $6/month or $50/year
- Unlimited trips
- Multiple bank accounts
- PDF export (react-pdf)
- Trip comparison table
- Cost-per-mile/night stats

### Stripe setup
- Checkout Session for upgrade
- Customer Portal for manage/cancel
- Webhook: `customer.subscription.updated` → update `subscriptions` table

---

## Phase 5 — Launch

### Landing page sections
1. Hero — hook + "Connect your bank" CTA
2. How it works — 3 steps (connect → plan → track)
3. Features — camping-specific categories, auto-import, cost stats
4. Pricing — free vs pro
5. Footer

### Pre-launch checklist
- [ ] Enable email confirmations (connect Resend for transactional email)
- [ ] Set up Plaid production account (requires approval, ~1-2 weeks)
- [ ] Add Vercel domain + SSL
- [ ] Set site URL in Supabase to production domain
- [ ] Add Google OAuth redirect URI for production domain
- [ ] LinkedIn launch post + demo video

---

## Key Files

```
src/
  pages/
    Login.jsx             ✅ Auth (Google + email)
    Dashboard.jsx         ✅ Trip list + budget bars
    TripNew.jsx           ✅ 2-step trip creation wizard
    TripDetail.jsx        ✅ Budget breakdown + cash quick-add
    TransactionReview.jsx ✅ Confirm/skip transaction queue
    ConnectBank.jsx       🔜 Plaid Link (stubbed, wire up Phase 2)
    Account.jsx           ✅ Profile + billing shell
  context/
    AuthContext.jsx       ✅ Auth state + helpers
  lib/
    supabase.js           ✅ Supabase client
    plaid.js              🔜 Phase 2
    categorize.js         🔜 Phase 2 — merchant → category rules
  backend/
    server.js             🔜 Phase 2 — Express + Plaid endpoints
supabase/
  migrations/
    001_init.sql          ✅ All tables + RLS + trigger
```

---

## Monetization math

| Users | Monthly revenue | Plaid cost | Net |
|---|---|---|---|
| 50 pro | $300 | ~$15 | ~$285 |
| 200 pro | $1,200 | ~$60 | ~$1,140 |
| 500 pro | $3,000 | ~$150 | ~$2,850 |

Plaid dev mode: free for first 100 Items. Production approval needed before public launch.

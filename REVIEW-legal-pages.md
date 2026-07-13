# Review Guide — `feature/legal-pages`

1 commit · 4 files · frontend only · client builds clean
Branched off **main** (independent of the transaction/refactor branches). Delete this file after merging.

## ⚠️ Legal content = your approval gate
This adds a **Privacy Policy** and **Terms of Service** required for Plaid production
access and app-store review. I drafted the text to match CampCost's *actual* data flow
(Plaid takes bank credentials directly, Supabase stores data, no data selling, 2FA before
bank connect, server-side access tokens). **It is a solid draft, not vetted legal advice** —
for an app touching bank data, get a legal pass (or a reputable generator reviewed against
Plaid's requirements) before you flip Plaid to production.

**Two placeholders you MUST set before launch:**
- `Terms.jsx` §11 Governing law → `[STATE — set before launch]`
- Confirm the operating entity/name (currently just "CampCost" + your email as contact)

## Read in this order
1. `client/src/pages/PrivacyPolicy.jsx` — the policy. Verify every claim is TRUE of your
   system (2FA required, tokens server-side, no ad trackers, Plaid link is correct).
2. `client/src/pages/Terms.jsx` — ToS. Check the disclaimers and the free/Pro plan wording
   match reality; set the governing-law state.
3. `client/src/App.jsx` — two **public** routes added (`/privacy`, `/terms`) — deliberately
   NOT behind `ProtectedRoute`, because they must load before login.
4. `client/src/components/Footer.jsx` — restructured: legal links now ALWAYS render (needed
   logged-out); the contact/LinkedIn line stays gated to signed-in users. Was `return null`
   when logged out — that would have hidden the legal links on the login page.

## Decisions I made
- **Content lives in JSX** (not markdown/CMS) — simplest, no new deps, matches your inline-style convention. Fine until you want non-dev edits.
- **Linked Plaid's end-user privacy policy** directly — Plaid requires you disclose their role.
- **Kept a warm-palette, readable layout** consistent with the rest of the app.

## Verify
- `cd client && npm run build` → succeeds (the >500kB chunk warning is pre-existing).
- Manually: run the client, visit `/privacy` and `/terms` **while logged out**, and click the footer links from the login page.

## Not done (by design)
- No backend, no DB changes, no acceptance-checkbox on signup. If Plaid/legal wants an
  explicit "I agree to Terms" at signup, that's a follow-up on `Login.jsx`.

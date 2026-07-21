# CampCost — Data Retention & Deletion Policy

**Owner:** Mason Scarby (Founder) — privacy@campcost.net
**Last reviewed:** 2026-07-21
**Review cadence:** Reviewed at least annually and whenever data handling changes.

CampCost collects only the data needed to provide its budgeting service and retains it
only as long as necessary. This policy describes what is stored, how long it is kept, and
how users can have it deleted.

## 1. What we store
| Data | Purpose | Source |
|---|---|---|
| Account (email, auth identity) | Login, account management | Supabase Auth |
| Plaid connection metadata + encrypted `access_token` | Sync bank transactions | Plaid (AES-256-GCM at rest) |
| Bank transactions (`transactions` table) | Trip budgeting, source of truth for spend | Plaid |
| Manual expenses, trips, budgets | User-entered budgeting data | User |
| Subscription/plan status | Billing entitlement | Stripe |

We do **not** store bank credentials — authentication with the financial institution is
handled entirely by Plaid.

## 2. Retention
- **Active accounts:** data is retained while the account is active and the user relies on
  it for budgeting history.
- **Financial history is soft-deleted, not hard-deleted,** during normal operation
  (e.g. a removed/flagged transaction sets `deleted_at`) so budgets stay auditable and
  correct. Soft-deleted rows are hidden everywhere via a global query filter.
- **Disconnecting a bank** detaches its transactions (`plaid_connection_id` set null) and
  **retains** the historical spend — it does not purge data (migration 008).

## 3. Deletion (user rights)
- Users may request access, correction, or **deletion** of their data by contacting
  privacy@campcost.net (also stated in the Privacy Policy).
- **Full account deletion** purges the user's stored transactions and connection records,
  and revokes/removes the associated Plaid item so no further data is pulled.
- Deletion requests are honored within 30 days.

## 4. Third-party data
- **Plaid:** on account deletion or bank disconnect, the corresponding Plaid item's access
  token is removed so CampCost no longer receives that user's financial data.
- **Stripe:** billing records are retained as required for financial/tax recordkeeping,
  then removed in line with Stripe's retention and applicable law.

## 5. Legal & compliance
- Retention and deletion practices are intended to comply with applicable data-privacy
  laws. Where law requires a minimum retention period (e.g. financial/tax records), that
  overrides deletion for the affected records only.

## 6. Review
This policy is reviewed at least annually and whenever CampCost's data handling,
storage, or third-party integrations change. The review date and owner are recorded at
the top of this document.

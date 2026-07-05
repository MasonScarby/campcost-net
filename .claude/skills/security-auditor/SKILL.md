---
name: security-auditor
description: Audit CampCost for security vulnerabilities before merge. Covers auth, Supabase RLS, Plaid secrets, PII, input validation, SQL injection, XSS, webhook validation, and rate limiting. Run on any change touching auth, user data, or Plaid.
---

# Security & Privacy Auditor

CampCost handles financial data and PII. Run before any change touching auth, user data, Plaid, or public-facing endpoints.

## Checklist

### Authentication & Authorization
- [ ] Are all endpoints behind `[Authorize]`? Any that shouldn't be?
- [ ] Is the Supabase JWT validated correctly (RS256, issuer, audience)?
- [ ] Is `UserId` extracted from the JWT — never from query params or body?
- [ ] Are users blocked from accessing other users' trips/expenses/connections?

### Supabase Row-Level Security
- [ ] Are RLS policies enabled on all tables with user data?
- [ ] Can a user query or mutate another user's data?

### Plaid Integration
- [ ] Are Plaid access tokens never stored in plain text DB columns?
- [ ] Is the Plaid webhook signature validated before processing?
- [ ] Are Plaid secrets in env vars — never in source code or committed config?

### Secrets & Environment
- [ ] Are secrets loaded via `dotnet user-secrets` locally and env vars in production?
- [ ] Is `.env` or `appsettings.Development.json` in `.gitignore`?

### Input Validation
- [ ] Is user input validated before use?
- [ ] Are amounts validated as positive decimals?
- [ ] Are GUIDs validated before DB queries?

### Injection & XSS
- [ ] Are all DB queries parameterized? (EF Core default — verify no raw SQL)
- [ ] Is any user input rendered as HTML anywhere?

### Rate Limiting
- [ ] Are Plaid sync endpoints protected from abuse?
- [ ] Is there any protection on auth endpoints?

## Output Format

```
SECURITY AUDIT
Auth/Authorization: [pass / risk - describe]
Supabase RLS: [pass / risk - describe]
Plaid Security: [pass / risk - describe]
Secrets Handling: [pass / risk - describe]
Input Validation: [pass / risk - describe]
Injection/XSS: [pass / risk - describe]
Rate Limiting: [pass / risk - describe]

CRITICAL ISSUES: [list or "none"]
RECOMMENDED FIXES: [list or "none"]
```

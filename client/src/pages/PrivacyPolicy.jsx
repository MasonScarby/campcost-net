import { Link } from 'react-router-dom'

// Public (unauthenticated) route. Plaid production access and app-store review both
// require a publicly reachable privacy policy URL, so this must NOT sit behind
// ProtectedRoute. Content is a DRAFT tailored to CampCost's actual data flow
// (Plaid + Supabase) — have it reviewed by counsel before enabling Plaid production.
const LAST_UPDATED = 'July 13, 2026'

const page = { maxWidth: 760, margin: '0 auto', padding: '40px 24px 80px', color: '#4a3f35', lineHeight: 1.65 }
const h1 = { fontSize: 30, color: '#5a3d28', marginBottom: 4 }
const h2 = { fontSize: 20, color: '#8b5e3c', marginTop: 34, marginBottom: 8 }
const muted = { fontSize: 13, color: '#b0a090' }

export default function PrivacyPolicy() {
  return (
    <div style={page}>
      <h1 style={h1}>Privacy Policy</h1>
      <p style={muted}>Last updated: {LAST_UPDATED}</p>

      <p style={{ marginTop: 20 }}>
        CampCost ("we", "us") helps you track trip spending by connecting your bank
        accounts and organizing transactions into trips and budgets. This policy explains
        what we collect, how we use it, and the choices you have. By using CampCost you
        agree to this policy and to our <Link to="/terms" style={{ color: '#8b5e3c' }}>Terms of Service</Link>.
      </p>

      <h2 style={h2}>Information we collect</h2>
      <ul>
        <li><strong>Account information</strong> — the email address you sign up with. Authentication is handled by Supabase; we never see or store your password.</li>
        <li><strong>Financial data (via Plaid)</strong> — when you connect a bank, our partner Plaid securely retrieves your transactions and account details (names, balances, transaction amounts, dates, and merchant/category data). <strong>Your bank login credentials go directly to Plaid — CampCost never sees or stores them.</strong></li>
        <li><strong>Data you enter</strong> — trips, destinations, dates, budgets, manual expenses, and category corrections.</li>
        <li><strong>Technical data</strong> — basic security and diagnostic logs (e.g. request timing, error events). We do not use third-party advertising or analytics trackers.</li>
      </ul>

      <h2 style={h2}>How we use your information</h2>
      <ul>
        <li>To display and categorize your spending, calculate budgets, and power trip tracking.</li>
        <li>To operate, secure, and troubleshoot the service.</li>
        <li>To contact you about your account when necessary.</li>
      </ul>
      <p><strong>We do not sell your personal or financial information, and we do not use it for advertising.</strong></p>

      <h2 style={h2}>How Plaid handles your data</h2>
      <p>
        We use Plaid Inc. to connect to your financial institution. Plaid's handling of
        your information is governed by Plaid's own privacy policy, available at{' '}
        <a href="https://plaid.com/legal/#end-user-privacy-policy" target="_blank" rel="noopener noreferrer" style={{ color: '#8b5e3c' }}>plaid.com/legal</a>.
        You can revoke Plaid's access at any time by disconnecting your bank in the app or
        through Plaid's portal.
      </p>

      <h2 style={h2}>Storage &amp; security</h2>
      <ul>
        <li>Data is stored in our Supabase (PostgreSQL) database. Access is restricted by row-level security keyed to your account.</li>
        <li>All traffic is encrypted in transit over HTTPS.</li>
        <li>Connecting a bank requires two-factor authentication (2FA) on your account.</li>
        <li>Bank access tokens are stored server-side and are never exposed to the browser.</li>
      </ul>

      <h2 style={h2}>Data sharing</h2>
      <p>
        We share data only with the service providers that make CampCost work — Plaid
        (bank connectivity), Supabase (database &amp; authentication), and our payment
        processor for subscriptions — and only as needed to provide the service. We may
        also disclose information if required by law.
      </p>

      <h2 style={h2}>Your rights &amp; choices</h2>
      <ul>
        <li>Access, correct, or delete your data by contacting us at the email below.</li>
        <li>Disconnect a bank at any time, which stops future transaction syncing.</li>
        <li>Delete your account, which removes your personal and financial data from our active systems.</li>
        <li>Depending on where you live (e.g. California/CCPA, EEA/GDPR), you may have additional rights over your data.</li>
      </ul>

      <h2 style={h2}>Data retention</h2>
      <p>
        We keep your data while your account is active. When you delete your account or
        request deletion, we remove your personal and financial data from active systems,
        except where we are required to retain limited records by law.
      </p>

      <h2 style={h2}>Children</h2>
      <p>CampCost is not intended for anyone under 18, and we do not knowingly collect data from children.</p>

      <h2 style={h2}>Changes to this policy</h2>
      <p>We may update this policy; material changes will be reflected by the "Last updated" date above.</p>

      <h2 style={h2}>Contact</h2>
      <p>
        Questions about your privacy? Email{' '}
        <a href="mailto:privacy@campcost.net" style={{ color: '#8b5e3c', fontWeight: 600 }}>privacy@campcost.net</a>.
      </p>
    </div>
  )
}

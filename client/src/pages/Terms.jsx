import { Link } from 'react-router-dom'

// Public (unauthenticated) route — see PrivacyPolicy.jsx for why. DRAFT tailored to
// CampCost; governing-law and entity details are placeholders Mason must set, and the
// whole document should get a legal pass before Plaid production.
const LAST_UPDATED = 'July 13, 2026'

const page = { maxWidth: 760, margin: '0 auto', padding: '40px 24px 80px', color: '#4a3f35', lineHeight: 1.65 }
const h1 = { fontSize: 30, color: '#5a3d28', marginBottom: 4 }
const h2 = { fontSize: 20, color: '#8b5e3c', marginTop: 34, marginBottom: 8 }
const muted = { fontSize: 13, color: '#b0a090' }

export default function Terms() {
  return (
    <div style={page}>
      <h1 style={h1}>Terms of Service</h1>
      <p style={muted}>Last updated: {LAST_UPDATED}</p>

      <p style={{ marginTop: 20 }}>
        These Terms govern your use of CampCost. By creating an account or using the
        service, you agree to these Terms and to our{' '}
        <Link to="/privacy" style={{ color: '#8b5e3c' }}>Privacy Policy</Link>. If you do
        not agree, do not use CampCost.
      </p>

      <h2 style={h2}>1. The service</h2>
      <p>
        CampCost is a personal budgeting tool that connects to your bank accounts (through
        Plaid) to help you track spending across trips. It is an informational tool only —
        it is <strong>not a bank, not a financial advisor, and does not provide financial,
        tax, or investment advice.</strong>
      </p>

      <h2 style={h2}>2. Eligibility</h2>
      <p>You must be at least 18 years old and able to form a binding contract to use CampCost.</p>

      <h2 style={h2}>3. Your account</h2>
      <ul>
        <li>You are responsible for keeping your login credentials secure and for all activity under your account.</li>
        <li>Two-factor authentication is required before connecting a bank account.</li>
        <li>Notify us promptly of any unauthorized use.</li>
      </ul>

      <h2 style={h2}>4. Bank connections</h2>
      <p>
        By connecting an account, you authorize CampCost and Plaid to access transaction
        and account information from your financial institution on your behalf. You can
        disconnect at any time. Your use of Plaid is also subject to Plaid's end-user
        agreement.
      </p>

      <h2 style={h2}>5. Acceptable use</h2>
      <p>You agree not to misuse the service — including attempting to access other users' data, disrupting the service, reverse-engineering it, or using it for any unlawful purpose.</p>

      <h2 style={h2}>6. Subscriptions &amp; billing</h2>
      <p>
        CampCost offers a free plan and a paid ("Pro") plan. Paid plans are billed through
        our payment processor. Plan features and limits (such as trip length and
        transaction caps on the free plan) are described in the app and may change with
        notice.
      </p>

      <h2 style={h2}>7. Accuracy disclaimer</h2>
      <p>
        Transaction and budget data is provided by third parties (your bank, via Plaid) and
        may be delayed, incomplete, or inaccurate. Always verify important figures against
        your bank's official records. CampCost is provided <strong>"as is" and "as
        available"</strong> without warranties of any kind.
      </p>

      <h2 style={h2}>8. Limitation of liability</h2>
      <p>
        To the fullest extent permitted by law, CampCost and its operator are not liable
        for any indirect, incidental, or consequential damages, or for any financial
        decisions you make based on information in the app.
      </p>

      <h2 style={h2}>9. Termination</h2>
      <p>You may stop using CampCost and delete your account at any time. We may suspend or terminate access if these Terms are violated.</p>

      <h2 style={h2}>10. Changes to these Terms</h2>
      <p>We may update these Terms; material changes are reflected by the "Last updated" date above. Continued use after changes means you accept them.</p>

      <h2 style={h2}>11. Governing law</h2>
      <p>
        These Terms are governed by the laws of the State of <strong>[STATE — set before launch]</strong>,
        without regard to its conflict-of-laws rules.
      </p>

      <h2 style={h2}>12. Contact</h2>
      <p>
        Questions about these Terms? Email{' '}
        <a href="mailto:hello@campcost.net" style={{ color: '#8b5e3c', fontWeight: 600 }}>hello@campcost.net</a>.
      </p>
    </div>
  )
}

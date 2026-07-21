import { useEffect, useState } from 'react'
import { getMfaStatus, enrollTotp, verifyTotp } from '../lib/mfa'

const btn = {
  background: '#2d5a27', color: 'white', border: 'none', borderRadius: 12,
  padding: '12px 20px', fontSize: 14, fontWeight: 600, cursor: 'pointer',
}

const codeInput = {
  width: '100%', border: '1px solid #e8d5b0', borderRadius: 12,
  padding: '10px 14px', fontSize: 18, letterSpacing: 6, textAlign: 'center',
  outline: 'none', boxSizing: 'border-box', fontFamily: 'inherit',
}

/**
 * Two-factor authentication (TOTP) setup + verification.
 *
 * Modes (detected automatically):
 *  - no verified factor  → enrollment: QR code / secret → verify a code
 *  - verified factor but session is aal1 → challenge: enter a code to upgrade to aal2
 *  - verified + aal2 → "enabled" state
 *
 * onReady() fires once the session reaches aal2 with a verified TOTP factor.
 */
export default function MfaSetup({ onReady }) {
  const [phase, setPhase] = useState('loading') // loading | enabled | enroll-start | enroll-verify | challenge | error
  const [enrollment, setEnrollment] = useState(null) // { factorId, qrCode, secret }
  const [challengeFactorId, setChallengeFactorId] = useState(null)
  const [code, setCode] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const [showSecret, setShowSecret] = useState(false)

  const refresh = async () => {
    try {
      const status = await getMfaStatus()
      if (status.hasVerifiedTotp && status.currentLevel === 'aal2') {
        setPhase('enabled')
        onReady?.()
      } else if (status.hasVerifiedTotp) {
        setChallengeFactorId(status.verifiedFactor.id)
        setPhase('challenge')
      } else {
        setPhase('enroll-start')
      }
    } catch (err) {
      setError(err.message)
      setPhase('error')
    }
  }

  useEffect(() => { refresh() }, []) // eslint-disable-line react-hooks/exhaustive-deps

  const startEnroll = async () => {
    setBusy(true)
    setError('')
    try {
      setEnrollment(await enrollTotp())
      setPhase('enroll-verify')
    } catch (err) {
      setError(err.message || 'Could not start 2FA enrollment. Is MFA enabled in Supabase?')
    }
    setBusy(false)
  }

  const submitCode = async (factorId) => {
    if (code.length !== 6) return
    setBusy(true)
    setError('')
    try {
      await verifyTotp(factorId, code)
      setCode('')
      await refresh()
    } catch (err) {
      setError(err.message || 'Invalid code — try again.')
    }
    setBusy(false)
  }

  if (phase === 'loading') {
    return <p style={{ fontSize: 13, color: '#8b5e3c', margin: 0 }}>Checking 2FA status...</p>
  }

  if (phase === 'enabled') {
    return (
      <div style={{ display: 'flex', alignItems: 'center', gap: 10 }}>
        <span style={{ fontSize: 11, background: '#dcfce7', color: '#15803d', padding: '3px 10px', borderRadius: 99, fontWeight: 600 }}>
          2FA enabled
        </span>
        <p style={{ fontSize: 13, color: '#8b5e3c', margin: 0 }}>
          Your account is protected with an authenticator app.
        </p>
      </div>
    )
  }

  if (phase === 'error') {
    return <p style={{ color: '#d4622a', fontSize: 13, margin: 0 }}>{error}</p>
  }

  return (
    <div>
      {phase === 'enroll-start' && (
        <>
          <p style={{ fontSize: 13, color: '#8b5e3c', marginBottom: 14 }}>
            Add an extra layer of security with an authenticator app
            (Google Authenticator, 1Password, Authy...). Required before connecting a bank.
          </p>
          <button onClick={startEnroll} disabled={busy} style={{ ...btn, opacity: busy ? 0.6 : 1 }}>
            {busy ? 'Setting up...' : 'Set up 2FA'}
          </button>
        </>
      )}

      {phase === 'enroll-verify' && enrollment && (
        <div style={{ display: 'flex', flexDirection: 'column', gap: 14 }}>
          <p style={{ fontSize: 13, color: '#4a3728', margin: 0 }}>
            1. Scan this QR code with your authenticator app:
          </p>
          <p style={{ fontSize: 12, color: '#8b5e3c', margin: 0 }}>
            Tip: Apple Passwords / Google Password Manager sync your codes across devices —
            use one so a lost phone doesn't lock you out. (Locked out anyway? Email campcost.founder@gmail.com.)
          </p>
          <img
            src={enrollment.qrCode}
            alt="TOTP QR code"
            style={{ width: 180, height: 180, alignSelf: 'center', background: 'white', borderRadius: 12, border: '1px solid #e8d5b0' }}
          />
          <button
            onClick={() => setShowSecret(s => !s)}
            style={{ background: 'none', border: 'none', color: '#8b5e3c', fontSize: 12, cursor: 'pointer', padding: 0, alignSelf: 'center' }}
          >
            {showSecret ? 'Hide setup key' : "Can't scan? Show setup key"}
          </button>
          {showSecret && (
            <code style={{ fontSize: 13, background: '#f9f5ee', borderRadius: 8, padding: '8px 12px', textAlign: 'center', wordBreak: 'break-all', color: '#4a3728' }}>
              {enrollment.secret}
            </code>
          )}
          <p style={{ fontSize: 13, color: '#4a3728', margin: 0 }}>
            2. Enter the 6-digit code from the app:
          </p>
          <input
            type="text"
            inputMode="numeric"
            maxLength={6}
            placeholder="000000"
            value={code}
            onChange={e => setCode(e.target.value.replace(/\D/g, ''))}
            onKeyDown={e => e.key === 'Enter' && submitCode(enrollment.factorId)}
            style={codeInput}
          />
          {error && <p style={{ color: '#d4622a', fontSize: 13, margin: 0 }}>{error}</p>}
          <button
            onClick={() => submitCode(enrollment.factorId)}
            disabled={busy || code.length !== 6}
            style={{ ...btn, opacity: busy || code.length !== 6 ? 0.5 : 1 }}
          >
            {busy ? 'Verifying...' : 'Verify & enable 2FA'}
          </button>
        </div>
      )}

      {phase === 'challenge' && (
        <div style={{ display: 'flex', flexDirection: 'column', gap: 14 }}>
          <p style={{ fontSize: 13, color: '#4a3728', margin: 0 }}>
            Enter the 6-digit code from your authenticator app:
          </p>
          <input
            type="text"
            inputMode="numeric"
            maxLength={6}
            placeholder="000000"
            value={code}
            onChange={e => setCode(e.target.value.replace(/\D/g, ''))}
            onKeyDown={e => e.key === 'Enter' && submitCode(challengeFactorId)}
            style={codeInput}
          />
          {error && <p style={{ color: '#d4622a', fontSize: 13, margin: 0 }}>{error}</p>}
          <button
            onClick={() => submitCode(challengeFactorId)}
            disabled={busy || code.length !== 6}
            style={{ ...btn, opacity: busy || code.length !== 6 ? 0.5 : 1 }}
          >
            {busy ? 'Verifying...' : 'Verify'}
          </button>
        </div>
      )}
    </div>
  )
}

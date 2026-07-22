import { useState, useEffect } from 'react'
import { useNavigate, Link } from 'react-router-dom'
import { useAuth } from '../context/AuthContext'
import { supabase } from '../lib/supabase'
import { getMfaStatus, verifyTotp } from '../lib/mfa'

// Landing page for the password-reset email link. Supabase parses the recovery token
// from the URL and emits a PASSWORD_RECOVERY event, giving a short-lived session in
// which updateUser({ password }) is allowed — BUT if the account has TOTP MFA, Supabase
// requires an aal2 session first, so we run the TOTP challenge before the password form.
export default function ResetPassword() {
  const { updatePassword } = useAuth()
  const navigate = useNavigate()
  const [ready, setReady] = useState(false)     // recovery session established?
  const [mfaFactorId, setMfaFactorId] = useState(null) // set → TOTP challenge required first
  const [mfaCode, setMfaCode] = useState('')
  const [password, setPassword] = useState('')
  const [confirm, setConfirm] = useState('')
  const [error, setError] = useState('')
  const [loading, setLoading] = useState(false)
  const [done, setDone] = useState(false)

  // Once we have a recovery session, check whether the account needs an aal2 (TOTP) step.
  const checkMfa = async () => {
    try {
      const { currentLevel, nextLevel, verifiedFactor } = await getMfaStatus()
      if (nextLevel === 'aal2' && currentLevel !== 'aal2' && verifiedFactor) {
        setMfaFactorId(verifiedFactor.id)
      }
    } catch {
      // If MFA status can't be read, fall through to the password form; Supabase will
      // still reject the update with an aal2 error, which we surface below.
    }
  }

  useEffect(() => {
    supabase.auth.getSession().then(({ data: { session } }) => {
      if (session) { setReady(true); checkMfa() }
    })
    const { data: { subscription } } = supabase.auth.onAuthStateChange((event, session) => {
      if (event === 'PASSWORD_RECOVERY' || session) { setReady(true); checkMfa() }
    })
    return () => subscription.unsubscribe()
  }, [])

  const handleMfaSubmit = async (e) => {
    e.preventDefault()
    setError('')
    setLoading(true)
    try {
      await verifyTotp(mfaFactorId, mfaCode) // elevates the session to aal2
      setMfaFactorId(null)                   // clear → reveal the password form
    } catch (err) {
      setError(err.message || 'Invalid code — try again.')
    }
    setLoading(false)
  }

  const handleSubmit = async (e) => {
    e.preventDefault()
    setError('')
    if (password.length < 8) { setError('Password must be at least 8 characters.'); return }
    if (password !== confirm) { setError('Passwords do not match.'); return }
    setLoading(true)
    const { error } = await updatePassword(password)
    setLoading(false)
    if (error) { setError(error.message); return }
    setDone(true)
    setTimeout(() => navigate('/dashboard'), 1500)
  }

  return (
    <div className="min-h-screen flex items-center justify-center bg-[#fdf6e8] px-4">
      <div className="w-full max-w-sm">
        <div className="text-center mb-8">
          <h1 className="text-3xl font-bold text-[#2d5a27]">CampCost</h1>
          <p className="text-[#8b5e3c] mt-1 text-sm">
            {mfaFactorId ? 'Confirm your identity' : 'Set a new password'}
          </p>
        </div>

        <div className="bg-white rounded-2xl border border-[#e8d5b0] shadow-sm" style={{ padding: '32px' }}>
          {done ? (
            <p className="text-[#2d5a27] text-sm text-center">
              Password updated. Signing you in…
            </p>
          ) : !ready ? (
            <div className="text-center">
              <p className="text-[#8b5e3c] text-sm">
                This reset link is invalid or has expired.
              </p>
              <Link to="/login" className="text-[#2d5a27] text-sm font-medium hover:underline" style={{ display: 'inline-block', marginTop: '12px' }}>
                Back to sign in
              </Link>
            </div>
          ) : mfaFactorId ? (
            <form onSubmit={handleMfaSubmit} style={{ display: 'flex', flexDirection: 'column', gap: '12px' }}>
              <p className="text-[#8b5e3c] text-xs">
                Enter the 6-digit code from your authenticator app to continue.
              </p>
              <input
                type="text"
                inputMode="numeric"
                autoComplete="one-time-code"
                placeholder="123456"
                value={mfaCode}
                onChange={e => setMfaCode(e.target.value.replace(/\D/g, '').slice(0, 6))}
                required
                autoFocus
                className="w-full border border-[#e8d5b0] rounded-xl text-sm outline-none focus:border-[#2d5a27] transition-colors tracking-widest text-center"
                style={{ padding: '12px 16px' }}
              />
              {error && <p className="text-[#d4622a] text-xs">{error}</p>}
              <button
                type="submit"
                disabled={loading || mfaCode.length !== 6}
                className="w-full bg-[#2d5a27] text-white rounded-xl text-sm font-medium hover:bg-[#3d7a36] disabled:opacity-50 transition-colors"
                style={{ padding: '12px 16px' }}
              >
                {loading ? 'Verifying…' : 'Verify'}
              </button>
            </form>
          ) : (
            <form onSubmit={handleSubmit} style={{ display: 'flex', flexDirection: 'column', gap: '12px' }}>
              <input
                type="password"
                placeholder="New password"
                value={password}
                onChange={e => setPassword(e.target.value)}
                required
                autoFocus
                className="w-full border border-[#e8d5b0] rounded-xl text-sm outline-none focus:border-[#2d5a27] transition-colors"
                style={{ padding: '12px 16px' }}
              />
              <input
                type="password"
                placeholder="Confirm new password"
                value={confirm}
                onChange={e => setConfirm(e.target.value)}
                required
                className="w-full border border-[#e8d5b0] rounded-xl text-sm outline-none focus:border-[#2d5a27] transition-colors"
                style={{ padding: '12px 16px' }}
              />
              {error && <p className="text-[#d4622a] text-xs">{error}</p>}
              <button
                type="submit"
                disabled={loading}
                className="w-full bg-[#2d5a27] text-white rounded-xl text-sm font-medium hover:bg-[#3d7a36] disabled:opacity-50 transition-colors"
                style={{ padding: '12px 16px' }}
              >
                {loading ? 'Saving…' : 'Update password'}
              </button>
            </form>
          )}
        </div>
      </div>
    </div>
  )
}

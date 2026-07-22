import { useState, useEffect } from 'react'
import { useNavigate, Link } from 'react-router-dom'
import { useAuth } from '../context/AuthContext'
import { supabase } from '../lib/supabase'

// Landing page for the password-reset email link. Supabase parses the recovery token
// from the URL and emits a PASSWORD_RECOVERY event, giving a short-lived session in
// which updateUser({ password }) is allowed. We gate the form on having that session.
export default function ResetPassword() {
  const { updatePassword } = useAuth()
  const navigate = useNavigate()
  const [ready, setReady] = useState(false)   // recovery session established?
  const [password, setPassword] = useState('')
  const [confirm, setConfirm] = useState('')
  const [error, setError] = useState('')
  const [loading, setLoading] = useState(false)
  const [done, setDone] = useState(false)

  useEffect(() => {
    // If we already have a session (link just processed), enable the form.
    supabase.auth.getSession().then(({ data: { session } }) => {
      if (session) setReady(true)
    })
    const { data: { subscription } } = supabase.auth.onAuthStateChange((event, session) => {
      if (event === 'PASSWORD_RECOVERY' || session) setReady(true)
    })
    return () => subscription.unsubscribe()
  }, [])

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
          <p className="text-[#8b5e3c] mt-1 text-sm">Set a new password</p>
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

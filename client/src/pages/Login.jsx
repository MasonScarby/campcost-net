import { useState } from 'react'
import { useNavigate, Link } from 'react-router-dom'
import { useAuth } from '../context/AuthContext'

export default function Login() {
  const [email, setEmail] = useState('')
  const [password, setPassword] = useState('')
  const [isSignUp, setIsSignUp] = useState(false)
  const [error, setError] = useState('')
  const [loading, setLoading] = useState(false)
  const { signIn, signUp, signInWithGoogle } = useAuth()
  const navigate = useNavigate()

  const handleSubmit = async (e) => {
    e.preventDefault()
    setError('')
    setLoading(true)
    const { error } = isSignUp
      ? await signUp(email, password)
      : await signIn(email, password)
    setLoading(false)
    if (error) {
      setError(error.message)
    } else {
      navigate('/dashboard')
    }
  }

  return (
    <div className="min-h-screen flex items-center justify-center bg-[#fdf6e8] px-4">
      <div className="w-full max-w-sm">
        <div className="text-center mb-8">
          <h1 className="text-3xl font-bold text-[#2d5a27]">CampCost</h1>
          <p className="text-[#8b5e3c] mt-1 text-sm">Track what the road really costs</p>
        </div>

        <div className="bg-white rounded-2xl border border-[#e8d5b0] shadow-sm" style={{ padding: '32px' }}>
          <h2 className="font-semibold text-[#4a3728]" style={{ marginBottom: '16px' }}>
            {isSignUp ? 'Create account' : 'Sign in'}
          </h2>

          <button
            onClick={signInWithGoogle}
            className="w-full flex items-center justify-center gap-2 border border-[#e8d5b0] rounded-xl text-sm font-medium text-[#4a3728] hover:bg-[#fdf6e8] transition-colors mb-4"
            style={{ padding: '14px 16px' }}
          >
            <svg width="18" height="18" viewBox="0 0 18 18"><path d="M17.64 9.2c0-.637-.057-1.251-.164-1.84H9v3.481h4.844c-.209 1.125-.843 2.078-1.796 2.717v2.258h2.908c1.702-1.567 2.684-3.875 2.684-6.615z" fill="#4285F4"/><path d="M9 18c2.43 0 4.467-.806 5.956-2.184l-2.908-2.258c-.806.54-1.837.86-3.048.86-2.344 0-4.328-1.584-5.036-3.711H.957v2.332A8.997 8.997 0 0 0 9 18z" fill="#34A853"/><path d="M3.964 10.707A5.41 5.41 0 0 1 3.682 9c0-.593.102-1.17.282-1.707V4.961H.957A8.996 8.996 0 0 0 0 9c0 1.452.348 2.827.957 4.039l3.007-2.332z" fill="#FBBC05"/><path d="M9 3.58c1.321 0 2.508.454 3.44 1.345l2.582-2.58C13.463.891 11.426 0 9 0A8.997 8.997 0 0 0 .957 4.961L3.964 7.293C4.672 5.163 6.656 3.58 9 3.58z" fill="#EA4335"/></svg>
            Continue with Google
          </button>

          <div className="relative mb-4">
            <div className="absolute inset-0 flex items-center">
              <div className="w-full border-t border-[#e8d5b0]" />
            </div>
            <div className="relative flex justify-center text-xs text-[#8b5e3c] bg-white px-2">
              or
            </div>
          </div>

          <form onSubmit={handleSubmit} style={{ display: 'flex', flexDirection: 'column', gap: '12px' }}>
            <input
              type="email"
              placeholder="Email"
              value={email}
              onChange={e => setEmail(e.target.value)}
              required
              className="w-full border border-[#e8d5b0] rounded-xl text-sm outline-none focus:border-[#2d5a27] transition-colors"
              style={{ padding: '12px 16px' }}
            />
            <input
              type="password"
              placeholder="Password"
              value={password}
              onChange={e => setPassword(e.target.value)}
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
              {loading ? 'Loading...' : isSignUp ? 'Create account' : 'Sign in'}
            </button>
          </form>

          <p className="text-center text-xs text-[#8b5e3c] mt-4">
            {isSignUp ? 'Already have an account?' : "Don't have an account?"}{' '}
            <button
              onClick={() => { setIsSignUp(!isSignUp); setError('') }}
              className="text-[#2d5a27] font-medium hover:underline"
            >
              {isSignUp ? 'Sign in' : 'Sign up'}
            </button>
          </p>
        </div>
      </div>
    </div>
  )
}

import { Link, useNavigate, useLocation } from 'react-router-dom'
import { useAuth } from '../context/AuthContext'

export default function Navbar() {
  const { user, signOut } = useAuth()
  const navigate = useNavigate()
  const location = useLocation()

  const handleSignOut = async () => {
    await signOut()
    navigate('/')
  }

  if (!user) return null

  const isActive = (path) => location.pathname === path ||
    (path !== '/dashboard' && location.pathname.startsWith(path))

  const navLink = (to, label, icon) => (
    <Link
      to={to}
      style={{
        display: 'flex',
        alignItems: 'center',
        gap: 6,
        fontSize: 14,
        fontWeight: 600,
        padding: '7px 14px',
        borderRadius: 10,
        textDecoration: 'none',
        transition: 'background 0.15s',
        background: isActive(to) ? '#2d5a27' : 'transparent',
        color: isActive(to) ? 'white' : '#4a3728',
      }}
    >
      {icon && <span style={{ fontSize: 16 }}>{icon}</span>}
      {label}
    </Link>
  )

  return (
    <nav style={{
      borderBottom: '1px solid #e8d5b0',
      background: 'rgba(255,255,255,0.92)',
      backdropFilter: 'blur(8px)',
      position: 'sticky',
      top: 0,
      zIndex: 50,
    }}>
      <div style={{
        maxWidth: 900,
        margin: '0 auto',
        padding: '0 20px',
        height: 56,
        display: 'flex',
        alignItems: 'center',
        justifyContent: 'space-between',
      }}>
        {/* Logo */}
        <Link
          to="/dashboard"
          style={{
            fontWeight: 800,
            fontSize: 20,
            color: '#2d5a27',
            textDecoration: 'none',
            letterSpacing: '-0.5px',
            display: 'flex',
            alignItems: 'center',
            gap: 8,
          }}
        >
          ⛺ CampCost
        </Link>

        {/* Nav links */}
        <div style={{ display: 'flex', alignItems: 'center', gap: 4 }}>
          {navLink('/dashboard', 'Trips', null)}
          {navLink('/connect-bank', 'Bank', null)}
          {navLink('/account', 'Account', null)}
          <div style={{ width: 1, height: 20, background: '#e8d5b0', margin: '0 8px' }} />
          <button
            onClick={handleSignOut}
            style={{
              background: 'none',
              border: '1px solid #e8d5b0',
              borderRadius: 10,
              padding: '6px 14px',
              fontSize: 14,
              fontWeight: 600,
              color: '#8b5e3c',
              cursor: 'pointer',
            }}
          >
            Sign out
          </button>
        </div>
      </div>
    </nav>
  )
}

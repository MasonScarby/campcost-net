import { useState, useEffect } from 'react'
import { Link, useNavigate, useLocation } from 'react-router-dom'
import { useAuth } from '../context/AuthContext'

// Collapse the nav to a hamburger at/below this width
function useIsMobile() {
  const query = '(max-width: 640px)'
  const [isMobile, setIsMobile] = useState(
    () => typeof window !== 'undefined' && window.matchMedia(query).matches
  )
  useEffect(() => {
    const mq = window.matchMedia(query)
    const handler = (e) => setIsMobile(e.matches)
    mq.addEventListener('change', handler)
    return () => mq.removeEventListener('change', handler)
  }, [])
  return isMobile
}

export default function Navbar() {
  const { user, signOut } = useAuth()
  const navigate = useNavigate()
  const location = useLocation()
  const isMobile = useIsMobile()
  const [menuOpen, setMenuOpen] = useState(false)

  // Close the mobile menu whenever the route changes
  useEffect(() => { setMenuOpen(false) }, [location.pathname])

  const handleSignOut = async () => {
    setMenuOpen(false)
    await signOut()
    navigate('/')
  }

  if (!user) return null

  const isActive = (path) => location.pathname === path ||
    (path !== '/dashboard' && location.pathname.startsWith(path))

  const navLink = (to, label, { block = false } = {}) => (
    <Link
      to={to}
      onClick={() => setMenuOpen(false)}
      style={{
        display: 'flex',
        alignItems: 'center',
        gap: 6,
        fontSize: 14,
        fontWeight: 600,
        padding: block ? '12px 14px' : '7px 14px',
        borderRadius: 10,
        textDecoration: 'none',
        transition: 'background 0.15s',
        background: isActive(to) ? '#2d5a27' : 'transparent',
        color: isActive(to) ? 'white' : '#4a3728',
        width: block ? '100%' : 'auto',
      }}
    >
      {label}
    </Link>
  )

  const signOutButton = (block = false) => (
    <button
      onClick={handleSignOut}
      style={{
        background: 'none',
        border: '1px solid #e8d5b0',
        borderRadius: 10,
        padding: block ? '12px 14px' : '6px 14px',
        fontSize: 14,
        fontWeight: 600,
        color: '#8b5e3c',
        cursor: 'pointer',
        width: block ? '100%' : 'auto',
        textAlign: block ? 'left' : 'center',
      }}
    >
      Sign out
    </button>
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
          onClick={() => setMenuOpen(false)}
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

        {/* Desktop nav links */}
        {!isMobile && (
          <div style={{ display: 'flex', alignItems: 'center', gap: 4 }}>
            {navLink('/dashboard', 'Trips')}
            {navLink('/connect-bank', 'Bank')}
            {navLink('/account', 'Account')}
            <div style={{ width: 1, height: 20, background: '#e8d5b0', margin: '0 8px' }} />
            {signOutButton(false)}
          </div>
        )}

        {/* Mobile hamburger toggle */}
        {isMobile && (
          <button
            onClick={() => setMenuOpen((open) => !open)}
            aria-label="Menu"
            aria-expanded={menuOpen}
            style={{
              background: 'none',
              border: '1px solid #e8d5b0',
              borderRadius: 10,
              width: 40,
              height: 40,
              display: 'flex',
              alignItems: 'center',
              justifyContent: 'center',
              fontSize: 18,
              color: '#4a3728',
              cursor: 'pointer',
            }}
          >
            {menuOpen ? '✕' : '☰'}
          </button>
        )}
      </div>

      {/* Mobile dropdown menu */}
      {isMobile && menuOpen && (
        <div style={{
          borderTop: '1px solid #e8d5b0',
          padding: '8px 20px 16px',
          display: 'flex',
          flexDirection: 'column',
          gap: 4,
        }}>
          {navLink('/dashboard', 'Trips', { block: true })}
          {navLink('/connect-bank', 'Bank', { block: true })}
          {navLink('/account', 'Account', { block: true })}
          <div style={{ height: 1, background: '#e8d5b0', margin: '8px 0' }} />
          {signOutButton(true)}
        </div>
      )}
    </nav>
  )
}
